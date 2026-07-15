// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.State.Pbt;

/// <summary>Identity of a world state: the block that produced it and its binary tree root.</summary>
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
