using System;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Optimism;

public class OptimismPoSSwitcher(ISpecProvider specProvider, IOptimismSpecHelper opSpecHelper) : IPoSSwitcher
{
    public UInt256? TerminalTotalDifficulty => specProvider.TerminalTotalDifficulty;

    public UInt256? FinalTotalDifficulty => 0;

    public bool TransitionFinished => true;

    public Hash256? ConfiguredTerminalBlockHash => null;

    public long? ConfiguredTerminalBlockNumber => null;

    public event EventHandler TerminalBlockReached { add { } remove { } }

    public void ForkchoiceUpdated(BlockHeader newHeadHash, Hash256 finalizedHash) { }

    public (bool IsTerminal, bool IsPostMerge) GetBlockConsensusInfo(BlockHeader header) => (false, IsPostMerge(header));

    public bool HasEverReachedTerminalBlock() => true;

    public bool IsPostMerge(BlockHeader header) => opSpecHelper.IsBedrock(header);

    public bool TryUpdateTerminalBlock(BlockHeader header)
    {
        throw new NotImplementedException("Should never be called in OP Stack chains");
    }
}
