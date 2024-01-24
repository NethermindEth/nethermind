// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Consensus.Validators;

public interface IBlockValidator : IHeaderValidator, IWithdrawalValidator
{
    bool ValidateOrphanedBlock(Block block, out string? error);
    bool ValidateSuggestedBlock(Block block);
    bool ValidateSuggestedBlock(Block block, out string? error);
    bool ValidateProcessedBlock(Block processedBlock, TxReceipt[] receipts, Block suggestedBlock);
    bool ValidateProcessedBlock(Block processedBlock, TxReceipt[] receipts, Block suggestedBlock, out string? error);
}
