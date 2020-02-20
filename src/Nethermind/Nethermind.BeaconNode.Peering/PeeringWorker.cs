//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nethermind.Core2;
using Nethermind.Logging.Microsoft;
using Nethermind.Peering.Mothra;

namespace Nethermind.BeaconNode.Peering
{
    public class PeeringWorker : BackgroundService
    {
        private readonly ILogger _logger;
        private readonly IHostEnvironment _environment;
        private readonly IClientVersion _clientVersion;
        private readonly IMothraLibp2p _mothraLibp2p;
        private bool _stopped;

        public PeeringWorker(ILogger<PeeringWorker> logger, IHostEnvironment environment, IClientVersion clientVersion, IMothraLibp2p mothraLibp2p)
        {
            _logger = logger;
            _environment = environment;
            _clientVersion = clientVersion;
            _mothraLibp2p = mothraLibp2p;
        }

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (_logger.IsDebug()) LogDebug.PeeringWorkerExecute(_logger, null);
            return Task.CompletedTask;
        }

        public async override Task StartAsync(CancellationToken cancellationToken)
        {
            if (_logger.IsInfo())
                Log.PeeringWorkerStarting(_logger, _clientVersion.Description,
                    _environment.EnvironmentName, Thread.CurrentThread.ManagedThreadId, null);

            try
            {
                _mothraLibp2p.PeerDiscovered += MothraLibp2pOnPeerDiscovered;
                _mothraLibp2p.GossipReceived += MothraLibp2pOnGossipReceived;
                _mothraLibp2p.RpcReceived += MothraLibp2pOnRpcReceived;

                //System.Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "nethermind/mothra";
                
                MothraSettings mothraSettings = new MothraSettings()
                {
                    //DataDirectory = "",
                    //BootNodes = {},
                    //Topics = { Topic.BeaconBlock }
                };
                _mothraLibp2p.Start(mothraSettings);

                if (_logger.IsDebug()) LogDebug.PeeringWorkerStarted(_logger, null);
            }
            catch (Exception ex)
            {
                if (_logger.IsError()) Log.PeeringWorkerCriticalError(_logger, ex);
            }

            await base.StartAsync(cancellationToken);
        }

        private void MothraLibp2pOnRpcReceived(object sender, RpcReceivedEventArgs e)
        {
            if (_logger.IsDebug()) LogDebug.RpcReceived(_logger, e.IsResponse ? "Response" : "Request", e.Method, e.Peer, e.Data.Length, null);
            // TODO: handle RPC
        }

        private void MothraLibp2pOnGossipReceived(object sender, GossipReceivedEventArgs e)
        {
            if (_logger.IsDebug()) LogDebug.GossipReceived(_logger, e.Topic, e.Data.Length, null);
            // TODO: handle topic
        }

        private void MothraLibp2pOnPeerDiscovered(object sender, PeerDiscoveredEventArgs e)
        {
            if (_logger.IsInfo()) Log.PeerDiscovered(_logger, e.Peer, null);
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            if (_logger.IsDebug()) LogDebug.PeeringWorkerStopping(_logger, null);
            _stopped = true;
            await base.StopAsync(cancellationToken);
        }
    }
}