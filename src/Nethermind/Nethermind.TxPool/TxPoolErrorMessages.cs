// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.TxPool;

public static class TxPoolErrorMessages
{
    // Geth-canonical mempool rejection phrases
    public const string AlreadyKnown = "already known";
    public const string TransactionUnderpriced = "transaction underpriced";
    public const string ReplacementTransactionUnderpriced = "replacement transaction underpriced";
    public const string GasLimitReached = "gas limit reached";
    public const string InsufficientFunds = "insufficient funds for gas * price + value";
    public const string NonceTooHigh = "nonce too high";
    public const string NonceTooLow = "nonce too low";
    public const string SenderNotEoa = "sender not an eoa";

    // Nethermind-specific mempool rejection phrases
    public const string FailedToRecoverSender = "failed to recover sender";
    public const string TransactionOverflow = "transaction cost overflow";
    public const string TransactionInvalid = "transaction invalid";
    public const string NonceTooFarInFuture = "nonce too far in future";
    public const string PendingTransactionTypeConflict = "pending transaction type conflict";
    public const string UnsupportedTransactionType = "unsupported transaction type";
    public const string TransactionTooLarge = "transaction too large";
    public const string DelegationNonceGap = "delegation nonce gap";
    public const string DelegationAuthorityHasPendingTx = "delegation authority has pending transaction";
    public const string NodeIsSyncing = "node is syncing";
}
