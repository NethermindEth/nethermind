// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Core;

/// <summary>
/// Stores the skip-list metadata for a block. <see cref="SkipCumulativeDifficulty"/> is the sum of
/// difficulties from the skip parent to this block (inclusive of skip parent, exclusive of self).
/// <see cref="Difficulty"/> is this block's own difficulty, cached to avoid deserializing the full header.
/// Both use <see cref="SignedUInt256"/> to support negative difficulty overrides (e.g. Optimism Bedrock TD reset).
/// <see cref="ParentHash"/> is the block's immediate parent (at self-1), distinct from
/// <see cref="SkipParentHash"/> which points SkipDistance blocks back.
/// The full total difficulty can be reconstructed by chaining along the skip path:
/// <c>TD(N) = Difficulty(N) + SkipCumDiff(N) + TD(skipParent(N))</c>.
/// </summary>
public readonly record struct SkipIndexedBlockInfoEntry(
    SignedUInt256 SkipCumulativeDifficulty,
    ValueHash256 SkipParentHash,
    SignedUInt256 Difficulty,
    ValueHash256 ParentHash);
