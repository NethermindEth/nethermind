// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;

namespace Nethermind.Eez.Follower;

/// <summary>
/// The forkchoice the follower drives, and its only writer. <see cref="Safe"/> is the last block L1 settled,
/// <see cref="Finalized"/> the last one settled in a finalized L1 block, and <see cref="Head"/> the latest block that
/// builds on safe: the last derived block or the sequencer's. Each move reaches the engine before it is recorded, so a
/// move the engine does not take leaves the heads as they were, and a move that would break
/// finalized &lt;= safe &lt;= head or take finalized back is refused.
/// </summary>
public sealed class FollowerHeads(IEezL2Engine engine, IBlockTree blockTree, BlockHeader finalized)
{
    public BlockHeader Head { get; private set; } = finalized;

    public BlockHeader Safe { get; private set; } = finalized;

    public BlockHeader Finalized { get; private set; } = finalized;

    /// <summary>Starts over from <paramref name="safe"/>, which is also the head: nothing above it is settled any more.</summary>
    public Task Reset(BlockHeader safe)
    {
        if (safe.Number < Finalized.Number)
        {
            throw Refused($"L1 no longer settles the finalized L2 block {Finalized.ToString(BlockHeader.Format.Short)}");
        }

        return Apply(safe, safe, Finalized);
    }

    public Task SetHead(BlockHeader head)
    {
        if (head.Number < Safe.Number)
        {
            throw Refused($"head {head.Number} would be below safe {Safe.Number}");
        }

        return Apply(head, Safe, Finalized);
    }

    /// <summary>Moves safe up to a block L1 settled; the head stays when it builds on it.</summary>
    public Task AdvanceSafe(BlockHeader safe)
    {
        if (safe.Number < Safe.Number)
        {
            throw Refused($"safe {safe.Number} would move back from {Safe.Number}");
        }

        BlockHeader head = Head.Number >= safe.Number && blockTree.IsMainChain(safe) ? Head : safe;
        return Apply(head, safe, Finalized);
    }

    /// <summary>Moves safe and the head back to <paramref name="parent"/>, before the block above it is replaced.</summary>
    public Task RetreatSafe(BlockHeader parent)
    {
        if (parent.Number < Finalized.Number)
        {
            throw Refused($"safe {parent.Number} would be below finalized {Finalized.Number}");
        }

        return Apply(parent, parent, Finalized);
    }

    public Task AdvanceFinalized(BlockHeader finalized)
    {
        if (finalized.Number < Finalized.Number || finalized.Number > Safe.Number)
        {
            throw Refused($"finalized {finalized.Number} would be outside {Finalized.Number} to safe {Safe.Number}");
        }

        return Apply(Head, Safe, finalized);
    }

    private async Task Apply(BlockHeader head, BlockHeader safe, BlockHeader finalized)
    {
        await engine.UpdateForkchoice(head.Hash!, safe.Hash!, finalized.Hash!);
        Head = head;
        Safe = safe;
        Finalized = finalized;
    }

    private static EezFollowerException Refused(string violation) => new($"The EEZ follower refuses a forkchoice: {violation}.");
}
