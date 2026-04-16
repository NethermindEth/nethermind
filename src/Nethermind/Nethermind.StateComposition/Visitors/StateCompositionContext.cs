// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Trie;

namespace Nethermind.StateComposition.Visitors;

public readonly struct StateCompositionContext(TreePath path, int level, bool isStorage, int? branchChildIndex, VisitorCounters? counters = null)
    : INodeContext<StateCompositionContext>
{
    public readonly TreePath Path = path;
    public readonly int Level = level;
    public readonly bool IsStorage = isStorage;
    public readonly int? BranchChildIndex = branchChildIndex;

    public readonly VisitorCounters? Counters = counters;

    public StateCompositionContext Add(ReadOnlySpan<byte> nibblePath)
    {
        return new StateCompositionContext(Path.Append(nibblePath), Level + 1, IsStorage, null, Counters);
    }

    public StateCompositionContext Add(byte nibble)
    {
        return new StateCompositionContext(Path.Append(nibble), Level + 1, IsStorage, nibble, Counters);
    }

    public StateCompositionContext AddStorage(in ValueHash256 storage)
    {
        return new StateCompositionContext(TreePath.Empty, 0, true, null, Counters);
    }
}
