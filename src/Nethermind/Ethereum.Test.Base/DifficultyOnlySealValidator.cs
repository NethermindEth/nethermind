// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus;
using Nethermind.Core;

namespace Ethereum.Test.Base;

/// <summary>
/// Test seal validator that skips PoW seal verification (too expensive for unit tests) but does
/// enforce the pre-Merge "header.Difficulty == calculated difficulty" rule via the supplied
/// <see cref="IDifficultyCalculator"/>. Mirrors the difficulty branch of
/// <c>EthashSealValidator.ValidateParams</c> without the Ethash hash work.
/// </summary>
internal sealed class DifficultyOnlySealValidator(IDifficultyCalculator difficultyCalculator) : ISealValidator
{
    private readonly IDifficultyCalculator _difficultyCalculator = difficultyCalculator ?? throw new ArgumentNullException(nameof(difficultyCalculator));

    public bool ValidateParams(BlockHeader parent, BlockHeader header, bool isUncle = false)
    {
        // Genesis (parent absent) carries the chainspec-declared difficulty; nothing to compare against.
        if (parent is null) return true;

        // Post-Merge difficulty rules are enforced by MergeHeaderValidator, not via the seal validator.
        if (header.Difficulty.IsZero) return true;

        return _difficultyCalculator.Calculate(header, parent) == header.Difficulty;
    }

    public bool ValidateSeal(BlockHeader header, bool force) => true;
}
