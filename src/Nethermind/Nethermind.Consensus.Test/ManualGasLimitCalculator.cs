// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;
using Nethermind.Core;

namespace Nethermind.Consensus.Test
{
    public class ManualGasLimitCalculator : IGasLimitCalculator
    {
        public long GasLimit { get; set; }
        public long GetGasLimit(BlockHeader parentHeader) => GasLimit;
        public long GetGasLimit(BlockHeader parentHeader, long? targetGasLimit) => GasLimit;
    }
}
