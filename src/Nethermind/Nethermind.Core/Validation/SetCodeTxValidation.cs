// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Messages;

namespace Nethermind.Core.Validation;

/// <summary>
/// Shape checks specific to <see cref="TxType.SetCode"/> (EIP-7702) transactions.
/// </summary>
/// <remarks>
/// These checks have to run on both the consensus side (via
/// <c>NoContractCreationTxValidator</c> / <c>AuthorizationListTxValidator</c>
/// registered in <c>TxValidator</c>) and the execution side
/// (<c>TransactionProcessor.ValidateStatic</c>) because RPC entrypoints such as
/// <c>eth_call</c> and <c>eth_estimateGas</c> reach the transaction processor
/// without going through the consensus validator. Keeping the predicates here
/// makes both sites delegate to a single source of truth and prevents the two
/// paths from drifting apart as EIP-7702 evolves.
/// </remarks>
public static class SetCodeTxValidation
{
    /// <summary>
    /// EIP-7702: a SetCode transaction cannot be a contract-creation transaction.
    /// </summary>
    public static ValidationResult ValidateNoContractCreation(Transaction transaction) =>
        transaction.IsContractCreation
            ? TxErrorMessages.NotAllowedCreateTransaction
            : ValidationResult.Success;

    /// <summary>
    /// EIP-7702: a SetCode transaction must carry a non-empty authorization list.
    /// </summary>
    public static ValidationResult ValidateAuthorizationList(Transaction transaction) =>
        transaction.AuthorizationList switch
        {
            null or { Length: 0 } => TxErrorMessages.MissingAuthorizationList,
            _ => ValidationResult.Success
        };
}
