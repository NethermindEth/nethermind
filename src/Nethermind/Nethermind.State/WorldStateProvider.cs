using Nethermind.Core;

namespace Nethermind.State;

public class WorldStateProvider(WorldState worldState, VerkleWorldState verkleWorldState, OverlayWorldState overlayWorldState)
{
    private ulong TransitionStartTimestamp { get; set; }
    private ulong TransitionEndTimestamp { get; set; }

    public IWorldState GetWorldState(Block block)
    {
        if (block.Timestamp < TransitionStartTimestamp) return worldState;
        if (block.Timestamp < TransitionEndTimestamp) return overlayWorldState;
        return verkleWorldState;
    }
}