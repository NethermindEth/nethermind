// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;

namespace Nethermind.State.Pbt.Test;

/// <summary>
/// A hand-built header chain standing in for the block tree, so a test can pin the root a scope is
/// made to report without wiring one up.
/// </summary>
internal sealed class PbtTestChildHeaders : IPbtChildHeaderSource
{
    private readonly Dictionary<Hash256, BlockHeader> _byParentHash = [];

    public BlockHeader? TryFindChild(BlockHeader parent) =>
        parent.Hash is null ? null : _byParentHash.GetValueOrDefault(parent.Hash);

    /// <summary>Appends the block that follows <paramref name="parent"/> and claims <paramref name="stateRoot"/>.</summary>
    public BlockHeader Add(BlockHeader parent, Hash256 stateRoot)
    {
        BlockHeader child = Build.A.BlockHeader.WithParent(parent).WithStateRoot(stateRoot).TestObject;
        _byParentHash[parent.Hash!] = child;
        return child;
    }
}
