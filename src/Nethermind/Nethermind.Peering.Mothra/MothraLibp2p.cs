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
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Nethermind.Peering.Mothra
{
    public class MothraLibp2p
    {
        private readonly Dictionary<LogLevel, string> _debugLevels = new Dictionary<LogLevel, string>()
        {
            [LogLevel.Trace] = "trace",
            [LogLevel.Debug] = "debug",
            [LogLevel.Information] = "info",
            [LogLevel.Warning] = "warn",
            [LogLevel.Error] = "error",
            [LogLevel.Critical] = "crit"
        };

        private MothraInterop.DiscoveredPeer _discoveredPeer;
        private GCHandle _discoveredPeerHandle;
        private MothraInterop.ReceiveGossip _receiveGossip;
        private GCHandle _receiveGossipHandle;
        private MothraInterop.ReceiveRpc _receiveRpc;
        private GCHandle _receiveRpcHandle;

        public event EventHandler<GossipReceivedEventArgs> GossipReceived;

        public event EventHandler<PeerDiscoveredEventArgs> PeerDiscovered;

        public event EventHandler<RpcReceivedEventArgs> RpcReceived;

        public void SendGossip()
        {
        }

        public void SendRpcRequest()
        {
        }

        public void SendRpcResponse()
        {
        }

        public void Start(MothraSettings settings)
        {
            RegisterHandlers();
            string[] args = BuildArgs(settings);
            MothraInterop.Start(args, args.Length);
        }

        private string[] BuildArgs(MothraSettings settings)
        {
            List<string> args = new List<string>();

            if (settings.DataDirectory != null)
            {
                args.Add("--datadir");
                args.Add($"'{settings.DataDirectory}'");
            }

            if (settings.ListenAddress != null)
            {
                args.Add("--listen-address");
                args.Add(settings.ListenAddress);
            }

            if (settings.MaximumPeers != null)
            {
                args.Add("--maxpeers");
                args.Add(settings.MaximumPeers.ToString());
            }

            if (settings.BootNodes.Count > 0)
            {
                args.Add("--boot-nodes");
                string value = string.Join(',', settings.BootNodes);
                args.Add(value);
            }

            if (settings.Port != null)
            {
                args.Add("--port");
                args.Add(settings.Port.ToString());
            }

            if (settings.DiscoveryPort != null)
            {
                args.Add("--discovery-port");
                args.Add(settings.DiscoveryPort.ToString());
            }

            if (settings.DiscoveryAddress != null)
            {
                args.Add("--discovery-address");
                args.Add(settings.DiscoveryAddress);
            }

            if (settings.Topics.Count > 0)
            {
                args.Add("--topics");
                string value = string.Join(',', settings.Topics);
                args.Add(value);
            }

            if (settings.PeerMultiAddresses.Count > 0)
            {
                args.Add("--libp2p-addresses");
                string value = string.Join(',', settings.PeerMultiAddresses);
                args.Add(value);
            }

            if (settings.DebugLevel != null)
            {
                if (_debugLevels.TryGetValue(settings.DebugLevel.Value, out string value))
                {
                    args.Add("--debug-level");
                    args.Add(value);
                }
            }

            if (settings.VerbosityLevel != null)
            {
                args.Add("--verbosity");
                args.Add(settings.VerbosityLevel.ToString());
            }

            return args.ToArray();
        }

        private unsafe void DiscoveredPeerHandler(byte* peerUtf8, int peerLength)
        {
            string peer = new String((sbyte*) peerUtf8, 0, peerLength, Encoding.UTF8);
            OnPeerDiscovered(new PeerDiscoveredEventArgs(peer));
        }

        private void OnGossipReceived(GossipReceivedEventArgs e)
        {
            GossipReceived?.Invoke(this, e);
        }

        private void OnPeerDiscovered(PeerDiscoveredEventArgs e)
        {
            PeerDiscovered?.Invoke(this, e);
        }

        private void OnRpcReceived(RpcReceivedEventArgs e)
        {
            RpcReceived?.Invoke(this, e);
        }

        private unsafe void ReceiveGossipHandler(byte* topicUtf8, int topicLength, byte* data, int dataLength)
        {
            Console.Write("dotnet: receive");
            string topic = new String((sbyte*) topicUtf8, 0, topicLength, Encoding.UTF8);
            string dataString = new String((sbyte*) data, 0, dataLength, Encoding.UTF8);
            Console.WriteLine($" gossip={topic},data={dataString}");
        }

        private unsafe void ReceiveRpcHandler(byte* methodUtf8, int methodLength, int requestResponseFlag,
            byte* peerUtf8,
            int peerLength, byte* data, int dataLength)
        {
            // Nothing
        }

        private unsafe void RegisterHandlers()
        {
            _discoveredPeer = new MothraInterop.DiscoveredPeer(DiscoveredPeerHandler);
            _receiveGossip = new MothraInterop.ReceiveGossip(ReceiveGossipHandler);
            _receiveRpc = new MothraInterop.ReceiveRpc(ReceiveRpcHandler);

            _discoveredPeerHandle = GCHandle.Alloc(_discoveredPeer);
            _receiveGossipHandle = GCHandle.Alloc(_receiveGossip);
            _receiveRpcHandle = GCHandle.Alloc(_receiveRpc);

            MothraInterop.RegisterHandlers(_discoveredPeer, _receiveGossip, _receiveRpc);
        }
    }
}