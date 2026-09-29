// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Eez.Follower;
using Nethermind.Eez.Sequencer;

namespace Nethermind.Eez.Test;

/// <summary>The sequencer's blocks one L2 block time apart, inserted and made the head as the real ones are.</summary>
internal sealed class FakeSequencedBlocks(ChainEngine engine) : ISequencedBlocks
{
    public ulong TimestampStep { get; set; } = 2;

    public List<BlockHeader> Empties { get; } = [];

    public async Task<BlockHeader> Live(BlockHeader parent, FollowerHeads heads, CancellationToken token) => await Commit(parent, heads);

    public async Task<BlockHeader> Empty(BlockHeader parent, FollowerHeads heads)
    {
        BlockHeader empty = await Commit(parent, heads);
        Empties.Add(empty);
        return empty;
    }

    private async Task<BlockHeader> Commit(BlockHeader parent, FollowerHeads heads)
    {
        Block block = Build.A.Block.WithParent(parent).WithTimestamp(parent.Timestamp + TimestampStep).TestObject;
        await engine.Insert(block);
        await heads.SetHead(block.Header);
        return block.Header;
    }
}
