// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.Tracing;

namespace Nethermind.Consensus.Producers
{
    public interface IBlockProducerInfo
    {
        IBlockProducer BlockProducer { get; }
        IManualBlockProductionTrigger BlockProductionTrigger { get; }
        IBlockTracer BlockTracer => NullBlockTracer.Instance;
    }
}
