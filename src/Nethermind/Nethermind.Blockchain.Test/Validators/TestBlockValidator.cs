// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Consensus.Validators;
using Nethermind.Core;

namespace Nethermind.Blockchain.Test.Validators;

public class TestBlockValidator : IBlockValidator
{
    public static TestBlockValidator AlwaysValid = new();
    public static TestBlockValidator NeverValid = new(false, false);
    private readonly Queue<bool> _processedValidationResults = null!;
    private readonly Queue<bool> _suggestedValidationResults = null!;
    private readonly bool? _alwaysSameResultForProcessed;
    private readonly bool? _alwaysSameResultForSuggested;

    public TestBlockValidator(bool suggestedValidationResult = true, bool processedValidationResult = true)
    {
        _alwaysSameResultForSuggested = suggestedValidationResult;
        _alwaysSameResultForProcessed = processedValidationResult;
    }

    public TestBlockValidator(Queue<bool> suggestedValidationResults, Queue<bool> processedValidationResults)
    {
        _suggestedValidationResults = suggestedValidationResults ?? throw new ArgumentNullException(nameof(suggestedValidationResults));
        _processedValidationResults = processedValidationResults ?? throw new ArgumentNullException(nameof(processedValidationResults));
    }

    public bool Validate(BlockHeader header, BlockHeader? parent, bool isUncle)
    {
        return _alwaysSameResultForSuggested ?? _suggestedValidationResults.Dequeue();
    }

    public bool Validate(BlockHeader header, BlockHeader? parent, bool isUncle, [NotNullWhen(false)] out string? error)
    {
        var result = _alwaysSameResultForSuggested ?? _suggestedValidationResults.Dequeue();
        error = result ? null : "";
        return result;
    }

    public bool Validate(BlockHeader header, bool isUncle)
    {
        return _alwaysSameResultForSuggested ?? _suggestedValidationResults.Dequeue();
    }
    public bool Validate(BlockHeader header, bool isUncle, [NotNullWhen(false)] out string? error)
    {
        error = null;
        return _alwaysSameResultForSuggested ?? _suggestedValidationResults.Dequeue();
    }

    public bool ValidateSuggestedBlock(Block block)
    {
        return _alwaysSameResultForSuggested ?? _suggestedValidationResults.Dequeue();
    }
    public bool ValidateSuggestedBlock(Block block, [NotNullWhen(false)] out string? error, bool validateHashes = true)
    {
        error = null;
        return _alwaysSameResultForSuggested ?? _suggestedValidationResults.Dequeue();
    }
    public bool ValidateProcessedBlock(Block processedBlock, TxReceipt[] receipts, Block suggestedBlock)
    {
        return _alwaysSameResultForProcessed ?? _processedValidationResults.Dequeue();
    }
    public bool ValidateProcessedBlock(Block processedBlock, TxReceipt[] receipts, Block suggestedBlock, [NotNullWhen(false)] out string? error)
    {
        error = null;
        return _alwaysSameResultForProcessed ?? _processedValidationResults.Dequeue();
    }

    public bool ValidateWithdrawals(Block block, out string? error)
    {
        error = null;

        return _alwaysSameResultForSuggested ?? _suggestedValidationResults.Dequeue();
    }

    public bool ValidateOrphanedBlock(Block block, [NotNullWhen(false)] out string? error)
    {
        error = null;
        return _alwaysSameResultForSuggested ?? _suggestedValidationResults.Dequeue();
    }

    public bool ValidateBodyAgainstHeader(BlockHeader header, BlockBody toBeValidated, [NotNullWhen(false)] out string? errorMessage)
    {
        errorMessage = null;
        return _alwaysSameResultForSuggested ?? _suggestedValidationResults.Dequeue();
    }
}
