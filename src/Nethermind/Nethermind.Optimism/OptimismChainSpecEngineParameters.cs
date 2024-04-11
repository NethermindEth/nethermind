// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Specs.ChainSpecStyle
{
    public class OptimismChainSpecEngineParameters : IChainSpecEngineParameters
    {
        public ulong RegolithTimestamp { get; set; }

        public long BedrockBlockNumber { get; set; }

        public ulong? CanyonTimestamp { get; set; }

        public Address? L1FeeRecipient { get; set; }

        public Address? L1BlockAddress { get; set; }

        public UInt256 CanyonBaseFeeChangeDenominator { get; set; }

        public Address? Create2DeployerAddress { get; set; }

        public byte[]? Create2DeployerCode { get; set; }

        public string ChainSpecItemName => "Optimism";

        public string SealEngineType => "Optimism";

        public void AddTransitions(SortedSet<long> blockNumbers, SortedSet<ulong> timestamps)
        {
            if (CanyonTimestamp is not null)
            {
                timestamps.Add(CanyonTimestamp.Value);
            }
        }

        public void AdjustReleaseSpec(ReleaseSpec spec, long startBlock, ulong? startTimestamp)
        {
            if (startTimestamp >= CanyonTimestamp)
            {
                spec.BaseFeeMaxChangeDenominator = CanyonBaseFeeChangeDenominator;
            }
        }
    }
}
