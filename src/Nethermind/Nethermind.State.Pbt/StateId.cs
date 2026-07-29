// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.State.Pbt;

/// <summary>Identifies one world state across the repository, the bundle cache and the persisted pointer.</summary>
/// <param name="StateRoot">
/// The root the block's header claims, which on a Patricia-rooted chain is not the EIP-8297 root PBT
/// folds. The header's root is what the rest of the node addresses a state by, so it is what keys a
/// state here; the tree's own root travels beside it on <see cref="PbtSnapshot.TreeRoot"/> and in the
/// persisted metadata.
/// </param>
public readonly record struct StateId(ulong BlockNumber, in ValueHash256 StateRoot)
{
    public StateId(BlockHeader? header) : this(header is null ? ulong.MaxValue : header.Number, header?.StateRoot ?? default)
    {
    }

    /// <summary>
    /// The state before genesis: an empty tree, whose EIP-8297 root is 32 zero bytes. The reserved
    /// height at the top of the block-number range cannot collide with a real state id.
    /// </summary>
    public static readonly StateId PreGenesis = new(ulong.MaxValue, default);
}
