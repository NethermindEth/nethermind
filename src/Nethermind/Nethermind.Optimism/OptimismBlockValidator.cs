// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Messages;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.TxPool;

namespace Nethermind.Optimism;

public class OptimismBlockValidator(
    ITxValidator txValidator,
    IHeaderValidator headerValidator,
    IUnclesValidator unclesValidator,
    ISpecProvider specProvider,
    IOptimismSpecHelper specHelper,
    ILogManager logManager) : BlockValidator(txValidator, headerValidator, unclesValidator, specProvider, logManager)
{
    private const string NonEmptyWithdrawalsList =
        $"{nameof(Block.Withdrawals)} is not an empty list";

    private const string MissingWithdrawalsRootError =
        $"{nameof(BlockHeader.WithdrawalsRoot)} is missing";

    private const string UnexpectedWithdrawalsRootError =
        $"{nameof(BlockHeader.WithdrawalsRoot)} is not 'keccak256(rlp(empty_string_code))'";

    /// <remarks>
    /// https://specs.optimism.io/protocol/isthmus/exec-engine.html#backwards-compatibility-considerations
    /// </remarks>
    private const string WithdrawalsRootOfEmptyError =
        $"{nameof(BlockHeader.WithdrawalsRoot)} is 'keccak256(rlp(empty_string_code))'";

    private const string NonNullWithdrawalsRootError =
        $"{nameof(BlockHeader.WithdrawalsRoot)} is not null";

    public override bool ValidateBodyAgainstHeader(BlockHeader header, BlockBody toBeValidated, out string? errorMessage)
    {
        if (!ValidateTxRootMatchesTxs(header, toBeValidated, out Hash256? txRoot))
        {
            errorMessage = BlockErrorMessages.InvalidTxRoot(header.TxRoot!, txRoot);
            return false;
        }

        if (!ValidateUnclesHashMatches(header, toBeValidated, out _))
        {
            errorMessage = BlockErrorMessages.InvalidUnclesHash;
            return false;
        }

        if (!ValidateWithdrawals(header, toBeValidated, out errorMessage))
        {
            return false;
        }

        errorMessage = null;
        return true;
    }

    protected override bool ValidateWithdrawals(Block block, IReleaseSpec spec, out string? error) =>
        ValidateWithdrawals(block.Header, block.Body, out error);

    private bool ValidateWithdrawals(BlockHeader header, BlockBody body, out string? error)
    {
        // From the most recent
        if (specHelper.IsIsthmus(header))
        {
            if (body.Withdrawals is null || body.Withdrawals.Length != 0)
            {
                error = NonEmptyWithdrawalsList;
                return false;
            }

            if (header.WithdrawalsRoot == null)
            {
                error = MissingWithdrawalsRootError;
                return false;
            }

            if (header.WithdrawalsRoot == Keccak.EmptyTreeHash)
            {
                error = WithdrawalsRootOfEmptyError;
                return false;
            }
        }
        else if (specHelper.IsCanyon(header))
        {
            if (header.WithdrawalsRoot != Keccak.EmptyTreeHash)
            {
                error = UnexpectedWithdrawalsRootError;
                return false;
            }
        }
        else
        {
            if (header.WithdrawalsRoot != null)
            {
                error = NonNullWithdrawalsRootError;
                return false;
            }
        }

        error = null;
        return true;
    }
}
