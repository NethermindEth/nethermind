// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.Consensus
{
    /// <summary>Block producer is setting current context and thanks to this context
    /// other classes like gas price comparison know what base fee they should be used</summary>
    public readonly struct BlockPreparationContext
    {
        public UInt256 BaseFee { get; }

        public long BlockNumber { get; }

        public BlockPreparationContext(in UInt256 baseFee, long blockNumber)
        {
            BaseFee = baseFee;
            BlockNumber = blockNumber;
        }
    }
}
