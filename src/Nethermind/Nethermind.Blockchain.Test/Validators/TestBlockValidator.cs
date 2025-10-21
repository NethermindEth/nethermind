// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Consensus.Validators;
using Nethermind.Core;

namespace Nethermind.Blockchain.Test.Validators;

public class TestBlockValidator(bool suggestedValidationResult = true) : IBlockValidator
{
    public static TestBlockValidator AlwaysValid = new();
    private readonly Queue<bool> _suggestedValidationResults = null!;
    private readonly bool? _alwaysSameResultForSuggested = suggestedValidationResult;

    public bool Validate(BlockHeader header, BlockHeader parent, bool isUncle, [NotNullWhen(false)] out string? error) => Validate(out error);
    public bool ValidateOrphaned(BlockHeader header, [NotNullWhen(false)] out string? error) => Validate(out error);
    public bool ValidateSuggestedBlock(Block block, BlockHeader parent, [NotNullWhen(false)] out string? error, bool validateHashes = true) => Validate(out error);
    public bool ValidateProcessedBlock(Block processedBlock, TxReceipt[] receipts, Block suggestedBlock, [NotNullWhen(false)] out string? error) => Validate(out error);
    public bool ValidateWithdrawals(Block block, out string? error) => Validate(out error);
    public bool ValidateOrphanedBlock(Block block, [NotNullWhen(false)] out string? error) => Validate(out error);
    public bool ValidateBodyAgainstHeader(BlockHeader header, BlockBody toBeValidated, [NotNullWhen(false)] out string? error) => Validate(out error);
    private bool Validate(out string? error)
    {
        var result = _alwaysSameResultForSuggested ?? _suggestedValidationResults.Dequeue();
        error = result ? null : "";
        return result;
    }
}
