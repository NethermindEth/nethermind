// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.BlockProduction;

namespace Nethermind.Merge.AuRa
{
    public class AuRaPostMergeBlockProducerFactory : PostMergeBlockProducerFactory
    {
        public AuRaPostMergeBlockProducerFactory(
            ISpecProvider specProvider,
            ISealEngine sealEngine,
            ITimestamper timestamper,
            IBlocksConfig blocksConfig,
            ILogManager logManager,
            IGasLimitCalculator? gasLimitCalculator = null)
            : base(
                specProvider,
                sealEngine,
                timestamper,
                blocksConfig,
                logManager,
                gasLimitCalculator)
        {
        }
    }
}
