// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.TxPool;

namespace Nethermind.Eez.Execution;

/// <summary>
/// EEZ L2 blocks carry no blob transactions and no beacon withdrawals: L2 ether is minted only by system transactions.
/// </summary>
public sealed class EezBlockValidator(
    ITxValidator txValidator,
    IHeaderValidator headerValidator,
    IUnclesValidator unclesValidator,
    ISpecProvider specProvider,
    ILogManager logManager) : BlockValidator(txValidator, headerValidator, unclesValidator, specProvider, logManager)
{
    internal const string BlobTransactionNotAllowed = "EEZ L2 blocks cannot contain blob transactions.";
    internal const string WithdrawalsNotAllowed = "EEZ L2 blocks cannot contain withdrawals.";

    protected override bool ValidateTransactions(Block block, IReleaseSpec spec, ref string? errorMessage)
    {
        Transaction[] transactions = block.Transactions;
        for (int i = 0; i < transactions.Length; i++)
        {
            if (transactions[i].SupportsBlobs || transactions[i].CarriesBlobs)
            {
                errorMessage = BlobTransactionNotAllowed;
                return false;
            }
        }

        return base.ValidateTransactions(block, spec, ref errorMessage);
    }

    protected override bool ValidateWithdrawals(Block block, IReleaseSpec spec, bool validateHashes, ref string? error)
    {
        if (block.Withdrawals is { Length: > 0 })
        {
            error = WithdrawalsNotAllowed;
            return false;
        }

        return base.ValidateWithdrawals(block, spec, validateHashes, ref error);
    }

    public override bool ValidateBodyAgainstHeader(BlockHeader header, BlockBody toBeValidated, out string? error)
    {
        if (toBeValidated.Withdrawals is { Length: > 0 })
        {
            error = WithdrawalsNotAllowed;
            return false;
        }

        return base.ValidateBodyAgainstHeader(header, toBeValidated, out error);
    }
}
