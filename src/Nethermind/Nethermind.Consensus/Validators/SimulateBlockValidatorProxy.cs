// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;

namespace Nethermind.Consensus.Validators;

public class SimulateBlockValidatorProxy(IBlockValidator baseBlockValidator) : IBlockValidator
{
    public bool ValidateWithdrawals(Block block, out string? error) =>
        baseBlockValidator.ValidateWithdrawals(block, out error);

    public bool ValidateOrphanedBlock(Block block, out string? error) =>
        baseBlockValidator.ValidateOrphanedBlock(block, out error);

    public bool ValidateSuggestedBlock(Block block, out string? error, bool validateHashes = true) =>
        baseBlockValidator.ValidateSuggestedBlock(block, out error, validateHashes);

    public bool ValidateProcessedBlock(Block processedBlock, TxReceipt[] receipts, Block suggestedBlock, out string? error)
    {
        error = "";
        return true;
    }

    public bool Validate(BlockHeader header, BlockHeader? parent, bool isUncle, out string? error) =>
        baseBlockValidator.Validate(header, parent, isUncle, out error);

    public bool Validate(BlockHeader header, bool isUncle, out string? error) =>
        baseBlockValidator.Validate(header, isUncle, out error);

    public bool ValidateBodyAgainstHeader(BlockHeader header, BlockBody toBeValidated, [NotNullWhen(false)] out string? errorMessage) =>
        baseBlockValidator.ValidateBodyAgainstHeader(header, toBeValidated, out errorMessage);
}
