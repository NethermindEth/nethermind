// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Consensus.Validators;

public class MultiCallBlockValidatorProxy : IBlockValidator
{
    private readonly IBlockValidator _baseBlockValidator;

    public MultiCallBlockValidatorProxy(IBlockValidator baseBlockValidator) =>
        _baseBlockValidator = baseBlockValidator;

    public bool Validate(BlockHeader header, BlockHeader? parent, bool isUncle = false) =>
        _baseBlockValidator.Validate(header, parent, isUncle);

    public bool Validate(BlockHeader header, bool isUncle = false) =>
        _baseBlockValidator.Validate(header, isUncle);

    public bool ValidateWithdrawals(Block block, out string? error) =>
        _baseBlockValidator.ValidateWithdrawals(block, out error);

    public bool ValidateOrphanedBlock(Block block, out string? error) =>
        return _baseBlockValidator.ValidateOrphanedBlock(block, out error);

    public bool ValidateSuggestedBlock(Block block) =>
        _baseBlockValidator.ValidateSuggestedBlock(block);

    public bool ValidateProcessedBlock(Block processedBlock, TxReceipt[] receipts, Block suggestedBlock) => true;
}
