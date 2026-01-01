using System;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Optimism;

public class OptimismPoSSwitcher(ISpecProvider specProvider, ulong bedrockBlockNumber) : IPoSSwitcher
{
    public OptimismPoSSwitcher(ISpecProvider specProvider, OptimismChainSpecEngineParameters optimismChainSpecEngineParameters)
        : this(specProvider, checked((ulong)optimismChainSpecEngineParameters.BedrockBlockNumber!.Value))
    {
    }

    public UInt256? TerminalTotalDifficulty => specProvider.TerminalTotalDifficulty;

    public UInt256? FinalTotalDifficulty => TerminalTotalDifficulty;

    public bool TransitionFinished => true;

    public Hash256? ConfiguredTerminalBlockHash => null;

    public long? ConfiguredTerminalBlockNumber => null;

    public event EventHandler TerminalBlockReached { add { } remove { } }

    public void ForkchoiceUpdated(BlockHeader newHeadHash, Hash256 finalizedHash) { }

    public (bool IsTerminal, bool IsPostMerge) GetBlockConsensusInfo(BlockHeader header)
    {
        bool isTerminal = bedrockBlockNumber > 0 && header.Number == bedrockBlockNumber - 1;
        return (isTerminal, header.IsPostMerge = header.Number >= bedrockBlockNumber);
    }

    public bool HasEverReachedTerminalBlock() => true;

    public bool IsPostMerge(BlockHeader header) => GetBlockConsensusInfo(header).IsPostMerge;

    public bool TryUpdateTerminalBlock(BlockHeader header) => false;
}
