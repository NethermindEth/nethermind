// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using Nethermind.Core.Crypto;

namespace Nethermind.Stats.Model
{
    public class SyncPeerNodeDetails
    {
        public byte ProtocolVersion { get; set; }
        public ulong NetworkId { get; set; }
        public BigInteger TotalDifficulty { get; set; }
        public Keccak BestHash { get; set; }
        public Keccak GenesisHash { get; set; }
    }
}
