// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Eez.Follower;

namespace Nethermind.Eez.Test;

/// <summary>The engine of a node that accepts every block it is not told to reject: it inserts it and moves the head, as the real engine does.</summary>
internal sealed class ChainEngine(IBlockTree chain) : IEezL2Engine
{
    public List<Block> Inserted { get; } = [];

    public List<(Hash256 Head, Hash256 Safe, Hash256 Finalized)> Forkchoices { get; } = [];

    public HashSet<Hash256> Rejected { get; } = [];

    public Task Insert(Block block)
    {
        if (Rejected.Contains(block.Hash!))
        {
            throw new EezFollowerException($"The node does not accept block {block.Number}.");
        }

        chain.SuggestBlock(block, BlockTreeSuggestOptions.None);
        Inserted.Add(block);
        return Task.CompletedTask;
    }

    public Task UpdateForkchoice(Hash256 head, Hash256 safe, Hash256 finalized)
    {
        Forkchoices.Add((head, safe, finalized));
        chain.TryUpdateMainChain(chain.FindHeader(head)!, true, true);
        return Task.CompletedTask;
    }

    public void EnsureSoleDriver()
    {
    }
}
