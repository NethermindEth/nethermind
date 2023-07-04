// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Network.P2P.ProtocolHandlers;

namespace Nethermind.Network.P2P.EventArg
{
    public class SyncPeerProtocolInitializedEventArgs : ProtocolInitializedEventArgs
    {
        public string Protocol { get; set; }
        public byte ProtocolVersion { get; set; }
        public ulong NetworkId { get; set; }
        public UInt256 TotalDifficulty { get; set; }
        public Keccak BestHash { get; set; }
        public Keccak GenesisHash { get; set; }
        public ForkId? ForkId { get; set; }

        public SyncPeerProtocolInitializedEventArgs(SyncPeerProtocolHandlerBase protocolHandler) : base(protocolHandler)
        {
        }
    }
}
