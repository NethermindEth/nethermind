using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Consensus;

public class AlwaysPoS : IPoSSwitcher
{
    private AlwaysPoS() { }

    private static AlwaysPoS _instance;
    public static AlwaysPoS Instance => LazyInitializer.EnsureInitialized(ref _instance, () => new());

    public UInt256? TerminalTotalDifficulty => 0;

    public UInt256? FinalTotalDifficulty => null;

    public bool TransitionFinished => true;

    public Hash256? ConfiguredTerminalBlockHash => null;

    public long? ConfiguredTerminalBlockNumber => 0;

#pragma warning disable CS0067
    public event EventHandler TerminalBlockReached;
#pragma warning restore CS0067

    public void ForkchoiceUpdated(BlockHeader newHeadHash, Hash256 finalizedHash) { }

    public (bool IsTerminal, bool IsPostMerge) GetBlockConsensusInfo(BlockHeader header) => (false, true);

    public bool HasEverReachedTerminalBlock() => true;

    public bool IsPostMerge(BlockHeader header) => true;

    public bool TryUpdateTerminalBlock(BlockHeader header) => false;
}
