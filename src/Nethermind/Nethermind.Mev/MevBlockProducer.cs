// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.Mev
{
    public class MevBlockProducer : MultipleBlockProducer<MevBlockProducer.MevBlockProducerInfo>
    {
        public MevBlockProducer(
            ILogManager logManager,
            params MevBlockProducerInfo[] blockProducers)
            : base(new MevBestBlockPicker(), logManager, blockProducers)
        {
        }

        private class MevBestBlockPicker : IBestBlockPicker
        {
            public Block? GetBestBlock(IEnumerable<(Block? Block, MevBlockProducerInfo BlockProducerInfo)> blocks)
            {
                Block? best = null;
                UInt256 maxBalance = UInt256.Zero;
                foreach ((Block? Block, MevBlockProducerInfo BlockProducerInfo) context in blocks)
                {
                    if (context.Block is not null)
                    {
                        BeneficiaryTracer beneficiaryTracer = context.BlockProducerInfo.BeneficiaryTracer;
                        UInt256 balance = beneficiaryTracer.BeneficiaryBalance;
                        if (balance > maxBalance || best is null)
                        {
                            best = context.Block;
                            maxBalance = balance;
                        }
                    }
                }

                return best;
            }
        }

        public class MevBlockProducerInfo : IBlockProducerInfo
        {
            public IBlockProducer BlockProducer { get; }
            public IBlockProductionCondition Condition { get; }
            public IBlockTracer BlockTracer => BeneficiaryTracer;
            public BeneficiaryTracer BeneficiaryTracer { get; }
            public MevBlockProducerInfo(
                IBlockProducer blockProducer,
                IBlockProductionCondition checkCondition,
                BeneficiaryTracer beneficiaryTracer)
            {
                BlockProducer = blockProducer;
                Condition = checkCondition;
                BeneficiaryTracer = beneficiaryTracer;
            }

        }
    }
}
