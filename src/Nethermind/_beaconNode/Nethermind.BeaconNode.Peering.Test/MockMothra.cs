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
using Nethermind.Peering.Mothra;

namespace Nethermind.BeaconNode.Peering.Test
{
    public class MockMothra : IMothraLibp2p
    {
        public IList<(byte[] methodUtf8, byte[] peerUtf8, byte[] data)> SendRpcRequestCalls =
            new List<(byte[] methodUtf8, byte[] peerUtf8, byte[] data)>();

        public IList<(byte[] methodUtf8, byte[] peerUtf8, byte[] data)> SendRpcResponseCalls =
            new List<(byte[] methodUtf8, byte[] peerUtf8, byte[] data)>();

        public IList<MothraSettings> StartCalls = new List<MothraSettings>();

        event GossipReceivedEventHandler? IMothraLibp2p.GossipReceived
        {
            add => _gossipReceived += value;
            remove => _gossipReceived -= value;
        }

        event PeerDiscoveredEventHandler? IMothraLibp2p.PeerDiscovered
        {
            add => _peerDiscovered += value;
            remove => _peerDiscovered -= value;
        }

        event RpcReceivedEventHandler? IMothraLibp2p.RpcReceived
        {
            add => _rpcReceived += value;
            remove => _rpcReceived -= value;
        }

        public event Action<byte[], byte[], byte[]>? SendRpcRequestCalled;

        public event Action<byte[], byte[], byte[]>? SendRpcResponseCalled;

        public event Action<MothraSettings>? StartCalled;

        private event GossipReceivedEventHandler? _gossipReceived;

        private event PeerDiscoveredEventHandler? _peerDiscovered;

        private event RpcReceivedEventHandler? _rpcReceived;

        public bool IsStarted
        {
            get { return true; }
        }

        public void RaiseGossipReceived(ReadOnlySpan<byte> topicUtf8, ReadOnlySpan<byte> data)
        {
            _gossipReceived?.Invoke(topicUtf8, data);
        }

        public void RaisePeerDiscovered(ReadOnlySpan<byte> peerUtf8)
        {
            _peerDiscovered?.Invoke(peerUtf8);
        }

        public void RaiseRpcReceived(ReadOnlySpan<byte> methodUtf8, int requestResponseFlag,
            ReadOnlySpan<byte> peerUtf8,
            ReadOnlySpan<byte> data)
        {
            _rpcReceived?.Invoke(methodUtf8, requestResponseFlag, peerUtf8, data);
        }

        bool IMothraLibp2p.SendGossip(ReadOnlySpan<byte> topicUtf8, ReadOnlySpan<byte> data)
        {
            throw new NotImplementedException();
        }

        bool IMothraLibp2p.SendRpcRequest(ReadOnlySpan<byte> methodUtf8, ReadOnlySpan<byte> peerUtf8,
            ReadOnlySpan<byte> data)
        {
            var bytes = (methodUtf8.ToArray(), peerUtf8.ToArray(), data.ToArray());
            SendRpcRequestCalls.Add(bytes);
            SendRpcRequestCalled?.Invoke(bytes.Item1, bytes.Item2, bytes.Item3);
            return true;
        }

        bool IMothraLibp2p.SendRpcResponse(ReadOnlySpan<byte> methodUtf8, ReadOnlySpan<byte> peerUtf8,
            ReadOnlySpan<byte> data)
        {
            var bytes = (methodUtf8.ToArray(), peerUtf8.ToArray(), data.ToArray());
            SendRpcResponseCalls.Add(bytes);
            SendRpcResponseCalled?.Invoke(bytes.Item1, bytes.Item2, bytes.Item3);
            return true;
        }

        void IMothraLibp2p.Start(MothraSettings settings)
        {
            StartCalls.Add(settings);
            StartCalled?.Invoke(settings);
        }
    }
}