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

    /// <summary>When set, each Live block carries one transaction with this many bytes of calldata.</summary>
    public int CalldataPerBlock { get; set; }

    public List<BlockHeader> Empties { get; } = [];

    public async Task<BlockHeader> Live(BlockHeader parent, FollowerHeads heads, CancellationToken token) =>
        await Commit(parent, heads, CalldataPerBlock == 0 ? [] : [Build.A.Transaction.WithNonce(parent.Number).WithData(new byte[CalldataPerBlock]).SignedAndResolved().TestObject]);

    public async Task<BlockHeader> Empty(BlockHeader parent, FollowerHeads heads)
    {
        BlockHeader empty = await Commit(parent, heads, []);
        Empties.Add(empty);
        return empty;
    }

    private async Task<BlockHeader> Commit(BlockHeader parent, FollowerHeads heads, Transaction[] transactions)
    {
        Block block = Build.A.Block.WithParent(parent).WithTimestamp(parent.Timestamp + TimestampStep).WithTransactions(transactions).TestObject;
        await engine.Insert(block);
        await heads.SetHead(block.Header);
        return block.Header;
    }
}
