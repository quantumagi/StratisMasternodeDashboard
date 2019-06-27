using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Stratis.FederatedSidechains.AdminDashboard.Entities;
using Stratis.FederatedSidechains.AdminDashboard.Helpers;
using Stratis.FederatedSidechains.AdminDashboard.Hubs;
using Stratis.FederatedSidechains.AdminDashboard.Models;
using Stratis.FederatedSidechains.AdminDashboard.Services;
using Stratis.FederatedSidechains.AdminDashboard.Settings;

namespace Stratis.FederatedSidechains.AdminDashboard.HostedServices
{
    /// <summary>
    /// This Background Service fetch APIs an cache content
    /// </summary>
    public class FetchingBackgroundService : IHostedService, IDisposable
    {
        private readonly DefaultEndpointsSettings defaultEndpointsSettings;
        private readonly IDistributedCache distributedCache;
        private readonly IHubContext<DataUpdaterHub> updaterHub;
        private readonly ILoggerFactory loggerFactory;
        private readonly ILogger<FetchingBackgroundService> logger;
        private readonly ApiRequester apiRequester;
        private bool successfullyBuilt;
        private Timer dataRetrieverTimer;
        private readonly bool is50K = true;

        public FetchingBackgroundService(IDistributedCache distributedCache, IOptions<DefaultEndpointsSettings> defaultEndpointsSettings, IHubContext<DataUpdaterHub> hubContext, ILoggerFactory loggerFactory, ApiRequester apiRequester, IConfiguration configuration)
        {
            this.distributedCache = distributedCache;
            this.updaterHub = hubContext;
            this.loggerFactory = loggerFactory;
            this.logger = loggerFactory.CreateLogger<FetchingBackgroundService>();
            this.apiRequester = apiRequester;
            this.defaultEndpointsSettings = configuration.GetSection("DefaultEndpoints").Get<DefaultEndpointsSettings>();
            if (this.defaultEndpointsSettings.SidechainNodeType == NodeTypes.TenK) this.is50K = false;
        }

        /// <summary>
        /// Start the Fetching Background Service to Populate Dashboard Datas
        /// </summary>
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            this.logger.LogInformation($"Starting the Fetching Background Service");

            DoWorkAsync(null);

            //TODO: Add timer setting in configuration file
            this.dataRetrieverTimer = new Timer(DoWorkAsync, null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
            await Task.CompletedTask;
        }

        /// <summary>
        /// Retrieve all node information and store it in IDistributedCache object
        /// </summary>
        private async Task BuildCacheAsync()
        {
            this.logger.LogInformation($"Refresh the Dashboard Data");

            NodeGetDataService nodeDataServiceMainchain;
            NodeGetDataService nodeDataServiceSidechain;

            if (this.is50K)
            {
                nodeDataServiceMainchain = new NodeGetDataServiceMultisig(this.apiRequester,
                    this.defaultEndpointsSettings.StratisNode, this.loggerFactory);
                nodeDataServiceSidechain = new NodeDataServiceSidechainMultisig(this.apiRequester, this.defaultEndpointsSettings.SidechainNode, this.loggerFactory);
            }
            else
            {
                nodeDataServiceMainchain = new NodeGetDataServiceMainchainMiner(this.apiRequester,
                    this.defaultEndpointsSettings.StratisNode, this.loggerFactory);
                nodeDataServiceSidechain = new NodeDataServicesSidechainMiner(this.apiRequester,
                    this.defaultEndpointsSettings.StratisNode, this.loggerFactory);
            }


            await nodeDataServiceMainchain.Update();
            await nodeDataServiceSidechain.Update();

            var stratisPeers = new List<Peer>();
            var stratisFederationMembers = new List<Peer>();
            var sidechainPeers = new List<Peer>();
            var sidechainFederationMembers = new List<Peer>();

            try
            {
                this.ParsePeers(nodeDataServiceMainchain.StatusResponse, nodeDataServiceMainchain.FedInfoResponse, ref stratisPeers, ref stratisFederationMembers);
                this.ParsePeers(nodeDataServiceSidechain.StatusResponse, nodeDataServiceSidechain.FedInfoResponse, ref sidechainPeers, ref sidechainFederationMembers);
            }
            catch(Exception e)
            {
                this.logger.LogError(e, "Unable to parse feeds");
            }

            var dashboardModel = new DashboardModel();
            try
            {
                dashboardModel.Status = true;
                dashboardModel.IsCacheBuilt = true;
                dashboardModel.MainchainWalletAddress = this.is50K ? ((NodeGetDataServiceMultisig)nodeDataServiceMainchain).FedAddress : String.Empty;
                dashboardModel.SidechainWalletAddress = this.is50K ? ((NodeDataServiceSidechainMultisig)nodeDataServiceSidechain).FedAddress : String.Empty;
                dashboardModel.MiningPublicKeys = nodeDataServiceMainchain.FedInfoResponse?.Content?.federationMultisigPubKeys ?? new JArray();

                StratisNodeModel stratisNode = new StratisNodeModel();

                stratisNode.History = this.is50K ? ((NodeGetDataServiceMultisig)nodeDataServiceMainchain).WalletHistory : new JArray();
                stratisNode.ConfirmedBalance = this.is50K ? ((NodeGetDataServiceMultisig)nodeDataServiceMainchain).WalletBalance.confirmedBalance : -1;
                stratisNode.UnconfirmedBalance = this.is50K ? ((NodeGetDataServiceMultisig)nodeDataServiceMainchain).WalletBalance.unconfirmedBalance : -1;

                
                stratisNode.WebAPIUrl = UriHelper.BuildUri(this.defaultEndpointsSettings.StratisNode, "/api").ToString();
                stratisNode.SwaggerUrl = UriHelper.BuildUri(this.defaultEndpointsSettings.StratisNode, "/swagger").ToString();
                stratisNode.SyncingStatus = nodeDataServiceMainchain.NodeStatus.SyncingProgress;
                stratisNode.Peers = stratisPeers;
                stratisNode.FederationMembers = stratisFederationMembers;
                stratisNode.BlockHash = nodeDataServiceMainchain.BestHash;
                stratisNode.BlockHeight = (int)nodeDataServiceMainchain.NodeStatus.BlockStoreHeight;
                stratisNode.MempoolSize = nodeDataServiceMainchain.RawMempool;

                stratisNode.CoinTicker = "STRAT";
                stratisNode.LogRules = nodeDataServiceMainchain.LogRules;
                stratisNode.Uptime = nodeDataServiceMainchain.NodeStatus.Uptime;

                dashboardModel.StratisNode = stratisNode;

                SidechainNodeModel sidechainNode = new SidechainNodeModel();

                sidechainNode.History = this.is50K ? ((NodeDataServiceSidechainMultisig)nodeDataServiceSidechain).WalletHistory : new JArray();
                sidechainNode.ConfirmedBalance = this.is50K ? ((NodeDataServiceSidechainMultisig)nodeDataServiceSidechain).WalletBalance.confirmedBalance : -1;
                sidechainNode.UnconfirmedBalance = this.is50K ? ((NodeDataServiceSidechainMultisig)nodeDataServiceSidechain).WalletBalance.unconfirmedBalance : -1;

                sidechainNode.WebAPIUrl = UriHelper.BuildUri(this.defaultEndpointsSettings.SidechainNode, "/api").ToString();
                sidechainNode.SwaggerUrl = UriHelper.BuildUri(this.defaultEndpointsSettings.SidechainNode, "/swagger").ToString();
                sidechainNode.SyncingStatus = nodeDataServiceSidechain.NodeStatus.SyncingProgress;
                sidechainNode.Peers = sidechainPeers;
                sidechainNode.FederationMembers = sidechainFederationMembers;
                sidechainNode.BlockHash = nodeDataServiceSidechain.BestHash;
                sidechainNode.BlockHeight = (int) nodeDataServiceSidechain.NodeStatus.BlockStoreHeight;
                sidechainNode.MempoolSize = nodeDataServiceSidechain.RawMempool;

                sidechainNode.CoinTicker = "TCRS";
                sidechainNode.LogRules = nodeDataServiceSidechain.LogRules;
                sidechainNode.PoAPendingPolls = this.defaultEndpointsSettings.SidechainNodeType.ToUpper() == NodeTypes.FiftyK ? nodeDataServiceSidechain.PendingPolls : null;
                sidechainNode.Uptime = nodeDataServiceSidechain.NodeStatus.Uptime;
                dashboardModel.SidechainNode = sidechainNode;
            }
            catch(Exception e)
            {
                this.logger.LogError(e, "Unable to fetch feeds.");
                return;
            }

            if (!string.IsNullOrEmpty(this.distributedCache.GetString("DashboardData")))
            {
                if (JToken.DeepEquals(this.distributedCache.GetString("DashboardData"), JsonConvert.SerializeObject(dashboardModel)) == false)
                {
                    await this.updaterHub.Clients.All.SendAsync("CacheIsDifferent");
                }
            }
            this.distributedCache.SetString("DashboardData", JsonConvert.SerializeObject(dashboardModel));
        }

        private void ParsePeers(dynamic stratisStatus, dynamic federationInfo, ref List<Peer> peers, ref List<Peer> federationMembers)
        {
            foreach (dynamic peer in (JArray)stratisStatus.Content.outboundPeers)
            {
                var endpointRegex = new Regex("\\[([A-Za-z0-9:.]*)\\]:([0-9]*)");
                MatchCollection endpointMatches = endpointRegex.Matches(Convert.ToString(peer.remoteSocketEndpoint));
                var endpoint = new IPEndPoint(IPAddress.Parse(endpointMatches[0].Groups[1].Value), int.Parse(endpointMatches[0].Groups[2].Value));

                string fedEndpoints = federationInfo?.Content?.endpoints?.ToString();
                
                if (!string.IsNullOrEmpty(fedEndpoints) && endpointMatches.Count > 0 && endpointMatches[0].Groups.Count > 1)
                {
                    var peerToAdd = new Peer
                    {
                        Endpoint = peer.remoteSocketEndpoint,
                        Type = "outbound",
                        Height = peer.tipHeight,
                        Version = peer.version
                    };
                    
                    if (fedEndpoints.Contains($"{endpoint.Address.MapToIPv4()}:{endpointMatches[0].Groups[2].Value}"))
                    {
                        federationMembers.Add(peerToAdd);
                    }
                    else
                    {
                        peers.Add(peerToAdd);
                    }
                }
            }
            foreach (dynamic peer in (JArray)stratisStatus.Content.inboundPeers)
            {
                var endpointRegex = new Regex("\\[([A-Za-z0-9:.]*)\\]:([0-9]*)");
                dynamic endpointMatches = endpointRegex.Matches(Convert.ToString(peer.remoteSocketEndpoint));
                var endpoint = new IPEndPoint(IPAddress.Parse(endpointMatches[0].Groups[1].Value), int.Parse(endpointMatches[0].Groups[2].Value));
                (Convert.ToString(federationInfo.Content.endpoints).Contains($"{endpoint.Address.MapToIPv4().ToString()}:{endpointMatches[0].Groups[2].Value}") ? federationMembers : peers)
                .Add(new Peer()
                {
                    Endpoint = peer.remoteSocketEndpoint,
                    Type = "inbound",
                    Height = peer.tipHeight,
                    Version = peer.version
                });
            }
        }

        private async void DoWorkAsync(object state)
        {
            if (this.PerformNodeCheck())
            {
                await this.BuildCacheAsync();
                this.successfullyBuilt = true;
            }
            else
            {
                await this.distributedCache.SetStringAsync("NodeUnavailable", "true");
                if (this.successfullyBuilt)
                {
                    await this.updaterHub.Clients.All.SendAsync("NodeUnavailable");
                }
                await this.distributedCache.RemoveAsync("DashboardData");
                this.successfullyBuilt = false;
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            this.logger.LogInformation($"Stopping the Fetching Background Service");
            this.dataRetrieverTimer?.Change(Timeout.Infinite, 0);
            return Task.CompletedTask;
        }

        /// <summary>
        /// When the service is disposed, the timer is disposed too
        /// </summary>
        public void Dispose()
        {
            this.dataRetrieverTimer?.Dispose();
        }

        /// <summary>
        /// Perform connection check with the nodes
        /// </summary>
        /// <remarks>The ports can be changed in the future</remarks>
        /// <returns>True if the connection are succeed</returns>
        private bool PerformNodeCheck()
        {
            var mainNodeUp = this.PortCheck(new Uri(this.defaultEndpointsSettings.StratisNode));
            var sidechainsNodeUp = this.PortCheck(new Uri(this.defaultEndpointsSettings.SidechainNode));
            return mainNodeUp && sidechainsNodeUp;
        } 

        /// <summary>
        /// Perform a TCP port scan
        /// </summary>
        /// <param name="port">Specify the port to scan</param>
        /// <returns>True if the port is opened</returns>
        private bool PortCheck(Uri endpointToCheck)
        {
            this.logger.LogInformation($"Perform a port check for {endpointToCheck.Host}:{endpointToCheck.Port}");
            using (var tcpClient = new TcpClient())
            {
                try
                {
                    this.logger.LogInformation($"Host {endpointToCheck.Host}:{endpointToCheck.Port} is available");
                    tcpClient.Connect(endpointToCheck.Host, endpointToCheck.Port);
                    return true;
                }
                catch
                {
                    this.logger.LogWarning($"Host {endpointToCheck.Host}:{endpointToCheck.Port} unavailable");
                    return false;
                }
            }
        }
    }
}