using System;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

public interface IWorldStateManager
{
    WorldState GetWorldState();
    VerkleWorldState GetVerkleWorldState();
    OverlayWorldState GetOverlayWorldState();

    IWorldState GetGlobalWorldState(Block block);
    IWorldState GetGlobalWorldState(UInt256 blockNumber);
    IWorldState GetGlobalWorldState(BlockHeader header);
    IWorldState GetGlobalWorldState(ulong timestamp);

    IStateReader GetGlobalStateReader(Block block);
    IStateReader GetGlobalStateReader(BlockHeader header);
    IStateReader GetGlobalStateReader(UInt256 blockNumber);
    IStateReader GetGlobalStateReader(ulong timestamp);
    IStateReader GetOverlayStateReader();

    /// <summary>
    /// Used by read only tasks that need to execute blocks.
    /// </summary>
    /// <returns></returns>
    IWorldState CreateResettableWorldState();

    event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached;
}