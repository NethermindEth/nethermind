using System;
using Nethermind.Consensus;
using Nethermind.Core;

namespace Nethermind.Optimism;

public class OptimismGasLimitCalculator : IGasLimitCalculator
{
    public long GetGasLimit(BlockHeader parentHeader) =>
        throw new InvalidOperationException("GasLimit in Optimism should come from payload attributes.");
}
