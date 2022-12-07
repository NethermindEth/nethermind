// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core2.Types;

namespace Nethermind.Core2.Eth1
{
    public class Eth1GenesisData
    {
        public Eth1GenesisData(Bytes32 blockHash, ulong timestamp)
        {
            BlockHash = blockHash;
            Timestamp = timestamp;
        }

        public Bytes32 BlockHash { get; }
        public ulong Timestamp { get; }
    }
}
