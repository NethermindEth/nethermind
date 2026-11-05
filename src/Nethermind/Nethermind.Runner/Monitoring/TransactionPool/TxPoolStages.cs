// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Runner.Monitoring.TransactionPool;

internal static class TxPoolStages
{
    public const string P2P = "P2P Network";
    public const string ReceivedTxs = "Received Txs";
    public const string NotSupportedTxType = "Not Supported Tx Type";
    public const string TxTooLarge = "Tx Too Large";
    public const string GasLimitTooHigh = "Gas Limit Too High";
    public const string TooLowPriorityFee = "Low Priority Fee";
    public const string TooLowFee = "Too Low Fee";
    public const string Malformed = "Malformed";
    public const string NullHash = "NullHash";
    public const string Duplicate = "Duplicate";
    public const string UnknownSender = "Unknown Sender";
    public const string ConflictingTxType = "Conflicting Tx Type";
    public const string NonceTooFarInFuture = "Nonce Too Far In Future";
    public const string StateValidation = "State Validation";
    public const string ZeroBalance = "Zero Balance";
    public const string BalanceLtTxValue = "Balance < Tx.Value";
    public const string BalanceTooLow = "Balance Too Low";
    public const string NonceUsed = "Nonce Used";
    public const string NoncesSkipped = "Nonces Skipped";
    public const string ValidationSucceeded = "Validation Succeeded";
    public const string FailedReplacement = "Failed Replacement";
    public const string CannotCompete = "Cannot Compete";
    public const string TransactionPool = "Tx Pool";
    public const string Evicted = "Evicted";
    public const string PrivateOrderFlow = "Private Order Flow";
    public const string AddedToBlock = "Added To Block";
    public const string ReorgedOut = "Reorged Out";
    public const string ReorgedIn = "Reorged In";
}
