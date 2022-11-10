// Copyright 2022 Demerzel Solutions Limited
// Licensed under the LGPL-3.0. For full terms, see LICENSE-LGPL in the project root.

using Nethermind.Core;

namespace Nethermind.Consensus.Validators;

public interface IBlockValidator : IHeaderValidator, IWithdrawalValidator
{
    bool ValidateSuggestedBlock(Block block);

    bool ValidateProcessedBlock(Block processedBlock, TxReceipt[] receipts, Block suggestedBlock);
}
