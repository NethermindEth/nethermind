// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Consensus.Validators;

public class SimulateBlockValidatorProxy(IBlockValidator baseBlockValidator) : IBlockValidator
{
    public bool Validate(BlockHeader header, BlockHeader? parent, bool isUncle = false) =>
        baseBlockValidator.Validate(header, parent, isUncle);

    public bool Validate(BlockHeader header, bool isUncle = false) =>
        baseBlockValidator.Validate(header, isUncle);

    public bool ValidateWithdrawals(Block block, out string? error) =>
        baseBlockValidator.ValidateWithdrawals(block, out error);

    public bool ValidateOrphanedBlock(Block block, out string? error) =>
        baseBlockValidator.ValidateOrphanedBlock(block, out error);

    public bool ValidateSuggestedBlock(Block block) =>
        baseBlockValidator.ValidateSuggestedBlock(block);

    public bool ValidateProcessedBlock(Block processedBlock, TxReceipt[] receipts, Block suggestedBlock) => true;
}
