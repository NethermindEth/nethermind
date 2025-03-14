using System;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Optimism;

public class OptimismPoSSwitcher(ISpecProvider specProvider, long bedrockBlockNumber) : IPoSSwitcher
{
    public OptimismPoSSwitcher(ISpecProvider specProvider, OptimismChainSpecEngineParameters optimismChainSpecEngineParameters)
        : this(specProvider, optimismChainSpecEngineParameters.BedrockBlockNumber!.Value)
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
        return (header.Number == bedrockBlockNumber - 1, header.IsPostMerge = header.Number >= bedrockBlockNumber);
    }

    public bool HasEverReachedTerminalBlock() => true;

    public bool IsPostMerge(BlockHeader header) => GetBlockConsensusInfo(header).IsPostMerge;

    public bool TryUpdateTerminalBlock(BlockHeader header) => false;
}
