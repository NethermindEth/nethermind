// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Blockchain.SkipIndexedBlockInfo;

/// <summary>
/// Identifies a block whose total difficulty is known from an external source (typically the configured
/// fast-sync pivot). Used by <see cref="SkipIndexedBlockInfoStore"/> to terminate skip-list walks that would
/// otherwise require pre-pivot headers missing on fast/snap-sync nodes.
/// </summary>
public readonly record struct TotalDifficultyAnchor(long Number, ValueHash256 Hash, UInt256 TotalDifficulty);

public interface ITotalDifficultyAnchor
{
    TotalDifficultyAnchor? TryGet();
}

public sealed class NullTotalDifficultyAnchor : ITotalDifficultyAnchor
{
    public static NullTotalDifficultyAnchor Instance { get; } = new();
    public TotalDifficultyAnchor? TryGet() => null;
}
