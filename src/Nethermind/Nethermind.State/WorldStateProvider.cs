using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.State;

public class WorldStateProvider
{
    public WorldState WorldState { get; set; }
    public VerkleWorldState VerkleWorldState { get; set; }
    public OverlayWorldState OverlayWorldState { get; set; }

    public WorldStateProvider(WorldState worldState, VerkleWorldState verkleWorldState, OverlayWorldState overlayWorldState)
    {
        WorldState = worldState;
        VerkleWorldState = verkleWorldState;
        OverlayWorldState = overlayWorldState;
    }

    private ulong TransitionStartTimestamp { get; set; }
    private ulong TransitionEndTimestamp { get; set; }

    public IWorldState GetWorldState(Block block)
    {
        if (block.Timestamp < TransitionStartTimestamp) return WorldState;
        if (block.Timestamp < TransitionEndTimestamp) return OverlayWorldState;
        return VerkleWorldState;
    }
    
    public void InitBranch(Block block, Hash256? branchStateRoot)
    {
        if (branchStateRoot is null) return;

        if (block.Timestamp < TransitionStartTimestamp)
        {
            if (branchStateRoot != WorldState.StateRoot)
            {
                WorldState.Reset();
                WorldState.StateRoot = branchStateRoot;
            }
        }

        if (block.Timestamp < TransitionEndTimestamp)
        {
            if (branchStateRoot != OverlayWorldState.StateRoot)
            {
                OverlayWorldState.Reset();
                OverlayWorldState.StateRoot = branchStateRoot;
            }
        }

        if (branchStateRoot != VerkleWorldState.StateRoot)
        {
            VerkleWorldState.Reset();
            VerkleWorldState.StateRoot = branchStateRoot;
        }
    }
}