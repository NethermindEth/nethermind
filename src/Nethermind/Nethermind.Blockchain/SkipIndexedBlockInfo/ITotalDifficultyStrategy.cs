// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Blockchain.SkipIndexedBlockInfo;

public interface ITotalDifficultyStrategy
{
    /// <summary>
    /// Returns the difficulty for a block as used by <see cref="SkipIndexedBlockInfoStore"/>.
    /// Default returns <see cref="BlockHeader.Difficulty"/>. Override to inject negative difficulty
    /// for TD resets (e.g. Optimism Bedrock).
    /// </summary>
    SignedUInt256 GetDifficulty(BlockHeader header) => (SignedUInt256)header.Difficulty;
}

public sealed class CumulativeTotalDifficultyStrategy : ITotalDifficultyStrategy
{
}

/// <summary>
/// Handles chains that reset TD to 0 at a known boundary block, most notably Optimism Mainnet at
/// the Bedrock transition. On OP Mainnet TD accumulates normally up to <c>Bedrock - 1</c> (reaching
/// <see cref="ISpecProvider.TerminalTotalDifficulty"/>) and then drops to 0 at the Bedrock block,
/// staying 0 forever after since all post-Bedrock blocks have <c>Difficulty = 0</c>. The inner
/// <paramref name="strategy"/> handles the normal per-block difficulty; this class injects a
/// one-off negative difficulty at the reset boundary so the skip-indexed store's cumulative sum
/// matches that semantic.
/// </summary>
public sealed class FixedTotalDifficultyStrategy(
    ITotalDifficultyStrategy strategy,
    long fixesBlockNumber,
    UInt256 toTotalDifficulty
) : ITotalDifficultyStrategy
{
    // At the block right after the fixed boundary, inject a negative difficulty so the
    // cumulative store resets TD from `toTotalDifficulty` to 0 (e.g. Optimism Bedrock).
    public SignedUInt256 GetDifficulty(BlockHeader header) =>
        header.Number > 0 && header.Number - 1 == fixesBlockNumber
            ? SignedUInt256.Negate(toTotalDifficulty)
            : strategy.GetDifficulty(header);
}
