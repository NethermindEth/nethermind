// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;

namespace Nethermind.Consensus.Validators
{
    public class NeverValidBlockValidator : IBlockValidator
    {
        public static bool ValidateHash(BlockHeader header)
        {
            return false;
        }

        public bool Validate(BlockHeader header, BlockHeader parent, bool isUncle)
        {
            return false;
        }
        public bool Validate(BlockHeader header, BlockHeader? parent, bool isUncle, out string? error)
        {
            error = null;
            return false;
        }

        public bool Validate(BlockHeader header, bool isUncle)
        {
            return false;
        }

        public bool Validate(BlockHeader header, bool isUncle, out string? error)
        {
            error = null;
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

        public bool ValidateOrphanedBlock(Block block, out string? error)
        {
            error = null;
            return false;
        }

        public bool ValidateSuggestedBlock(Block block, out string? error, bool validateHashes = true)
        {
            error = null;
            return false;
        }

        public bool ValidateProcessedBlock(Block processedBlock, TxReceipt[] receipts, Block suggestedBlock, out string? error)
        {
            error = null;
            return false;
        }

        public bool ValidateBodyAgainstHeader(BlockHeader header, BlockBody toBeValidated, [NotNullWhen(false)] out string? errorMessage)
        {
            errorMessage = null;
            return false;
        }
    }
}
