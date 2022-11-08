// Copyright 2022 Demerzel Solutions Limited
// Licensed under the LGPL-3.0. For full terms, see LICENSE-LGPL in the project root.

using Nethermind.Core;

namespace Nethermind.Consensus.Validators
{
    public class NeverValidBlockValidator : IBlockValidator
    {
        public bool ValidateHash(BlockHeader header)
        {
            return false;
        }

        public bool Validate(BlockHeader header, BlockHeader parent, bool isUncle)
        {
            return false;
        }

        public bool Validate(BlockHeader header, bool isUncle)
        {
            return false;
        }

        public bool ValidateSuggestedBlock(Block block)
        {
            return false;
        }

        public bool ValidateProcessedBlock(Block processedBlock, TxReceipt[] receipts, Block suggestedBlock)
        {
            return false;
        }

        public bool ValidateWithdrawals(Block block, out string? error)
        {
            error = null;

            return false;
        }
    }
}
