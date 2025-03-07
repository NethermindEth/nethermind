// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.State;

namespace Nethermind.Optimism;

/// <summary>
/// https://github.com/ethereum-optimism/specs/blob/main/specs/protocol/isthmus/exec-engine.md#l2tol1messagepasser-storage-root-in-header
/// </summary>
/// <param name="specHelper"></param>
public class OptimismWithdrawalValidator(IStateReader reader, IOptimismSpecHelper specHelper)
{
    private static class ErrorMessages
    {
        public const string MissingWithdrawalsRoot = $"{nameof(BlockHeader.WithdrawalsRoot)} is missing";
        public const string MissingL2ToL1MessagePasser = $"{nameof(PreDeploys.L2ToL1MessagePasser)} is missing";
        public const string WithdrawalsRootMismatch = $"{nameof(BlockHeader.WithdrawalsRoot)} mismatch";
        public const string WithdrawalsRootShouldBeOfEmptyString = $"{nameof(BlockHeader.WithdrawalsRoot)} should be keccak256(rlp(empty_string_code))";
        public const string WithdrawalsRootShouldBeNull = $"{nameof(BlockHeader.WithdrawalsRoot)} should be null";
    }

    public bool ValidateBefore(BlockHeader header, out string? error)
    {
        // From the most recent
        if (specHelper.IsIsthmus(header))
        {
            if (header.WithdrawalsRoot == null)
            {
                error = ErrorMessages.MissingWithdrawalsRoot;
                return false;
            }

            // The withdrawals root should be checked only after the state transition. This can't be done before.
            error = null;
            return true;
        }

        if (specHelper.IsCanyon(header))
        {
            if (header.WithdrawalsRoot == null)
            {
                error = ErrorMessages.MissingWithdrawalsRoot;
                return false;
            }

            if (header.WithdrawalsRoot != Keccak.OfAnEmptySequenceRlp)
            {
                error = ErrorMessages.WithdrawalsRootShouldBeOfEmptyString;
                return false;
            }

            error = null;
            return true;
        }

        // prior Canyon
        if (header.WithdrawalsRoot != null)
        {
            error = ErrorMessages.WithdrawalsRootShouldBeNull;
            return false;
        }

        error = null;
        return true;
    }

    public bool ValidateAfter(BlockHeader header, out string? error)
    {
        // From the most recent
        if (specHelper.IsIsthmus(header))
        {
            if (!reader.TryGetAccount(header.StateRoot!, PreDeploys.L2ToL1MessagePasser, out var account))
            {
                error = ErrorMessages.MissingL2ToL1MessagePasser;
                return false;
            }

            if (header.WithdrawalsRoot != account.StorageRoot)
            {
                error = ErrorMessages.WithdrawalsRootMismatch;
                return false;
            }
        }

        error = null;
        return true;
    }
}
