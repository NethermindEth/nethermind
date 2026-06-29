// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.TxPool
{
    /// <summary>
    /// Describes potential outcomes of adding transaction to the TX pool.
    /// </summary>
    public readonly struct AcceptTxResult(int id, string code, string? message = null) : IEquatable<AcceptTxResult>
    {
        /// <summary>
        /// The transaction has been accepted. This is the only 'success' outcome.
        /// </summary>
        public static readonly AcceptTxResult Accepted = new(0, nameof(Accepted));

        /// <summary>
        /// A transaction with the same hash has already been added to the pool in the past.
        /// </summary>
        public static readonly AcceptTxResult AlreadyKnown = new(1, TxPoolErrorMessages.AlreadyKnown);

        /// <summary>
        /// Covers scenarios where sender recovery fails.
        /// </summary>
        public static readonly AcceptTxResult FailedToResolveSender = new(2, TxPoolErrorMessages.FailedToRecoverSender);

        /// <summary>
        /// Fee paid by this transaction is not enough to be accepted in the mempool.
        /// </summary>
        public static readonly AcceptTxResult FeeTooLow = new(3, TxPoolErrorMessages.TransactionUnderpriced);

        /// <summary>
        /// Fee paid by this transaction is not enough to be accepted in the mempool.
        /// </summary>
        public static readonly AcceptTxResult FeeTooLowToCompete = new(4, TxPoolErrorMessages.ReplacementTransactionUnderpriced);

        /// <summary>
        /// Transaction gas limit exceeds the block gas limit.
        /// </summary>
        public static readonly AcceptTxResult GasLimitExceeded = new(5, TxPoolErrorMessages.GasLimitReached);

        /// <summary>
        /// Sender account has not enough balance to execute this transaction.
        /// </summary>
        public static readonly AcceptTxResult InsufficientFunds = new(6, TxPoolErrorMessages.InsufficientFunds);

        /// <summary>
        /// Calculation of gas price * gas limit + value overflowed int256.
        /// </summary>
        public static readonly AcceptTxResult Int256Overflow = new(7, TxPoolErrorMessages.TransactionOverflow);

        /// <summary>
        /// Transaction format is invalid.
        /// </summary>
        public static readonly AcceptTxResult Invalid = new(8, TxPoolErrorMessages.TransactionInvalid);

        /// <summary>
        /// The nonce is not the next nonce after the last nonce of this sender present in TxPool.
        /// </summary>
        public static readonly AcceptTxResult NonceGap = new(9, TxPoolErrorMessages.NonceTooHigh);

        /// <summary>
        /// The EOA (externally owned account) that signed this transaction (sender) has already signed and executed a transaction with the same nonce.
        /// </summary>
        public static readonly AcceptTxResult OldNonce = new(10, TxPoolErrorMessages.NonceTooLow);

        /// <summary>
        /// Transaction is not allowed to replace the one already in the pool. Fee bump is too low or some requirements are not fulfilled
        /// </summary>
        public static readonly AcceptTxResult ReplacementNotAllowed = new(11, TxPoolErrorMessages.ReplacementTransactionUnderpriced);

        /// <summary>
        /// Transaction sender has code hash that is not null.
        /// </summary>
        public static readonly AcceptTxResult SenderIsContract = new(12, TxPoolErrorMessages.SenderNotEoa);

        /// <summary>
        /// The nonce is too far in the future.
        /// </summary>
        public static readonly AcceptTxResult NonceTooFarInFuture = new(13, TxPoolErrorMessages.NonceTooFarInFuture);

        /// <summary>
        /// Ignores blob transactions if sender already have pending transactions of other types; ignore other types if has already pending blobs
        /// </summary>
        public static readonly AcceptTxResult PendingTxsOfConflictingType = new(14, TxPoolErrorMessages.PendingTransactionTypeConflict);

        /// <summary>
        /// Ignores transactions if tx type is not supported
        /// </summary>
        public static readonly AcceptTxResult NotSupportedTxType = new(15, TxPoolErrorMessages.UnsupportedTransactionType);

        /// <summary>
        /// Transaction size exceeds configured max size.
        /// </summary>
        public static readonly AcceptTxResult MaxTxSizeExceeded = new(16, TxPoolErrorMessages.TransactionTooLarge);

        /// <summary>
        /// Only one tx with current state matching nonce is allowed per delegated account or pending delegation.
        /// </summary>
        public static readonly AcceptTxResult NotCurrentNonceForDelegation = new(17, TxPoolErrorMessages.DelegationNonceGap);

        /// <summary>
        /// There is a pending transaction from a delegation in the tx pool already.
        /// </summary>
        public static readonly AcceptTxResult DelegatorHasPendingTx = new(18, TxPoolErrorMessages.DelegationAuthorityHasPendingTx);

        /// <summary>
        /// The node is syncing and cannot accept transactions at this time.
        /// </summary>
        public static readonly AcceptTxResult Syncing = new(503, TxPoolErrorMessages.NodeIsSyncing);

        /// <summary>
        /// The signer could not produce a signature for the transaction (locked account, missing key, remote signer rejection).
        /// </summary>
        public static readonly AcceptTxResult SignFailed = new(19, nameof(SignFailed), "authentication needed: password or unlock");

        private int Id { get; } = id;
        private string Code { get; } = code;
        private string? Message { get; } = message;

        public static implicit operator bool(AcceptTxResult result) => result.Id == Accepted.Id;
        public static implicit operator AcceptTxResult(bool result) => result ? Accepted : Invalid;
        public AcceptTxResult WithMessage(string message) => new(Id, Code, message);
        public static bool operator ==(AcceptTxResult a, AcceptTxResult b) => a.Equals(b);
        public static bool operator !=(AcceptTxResult a, AcceptTxResult b) => !(a == b);
        public override bool Equals(object? obj) => obj is AcceptTxResult result && Equals(result);
        public bool Equals(AcceptTxResult result) => Id == result.Id;
        public override int GetHashCode() => Id.GetHashCode();
        public override string ToString() => Message is null ? Code : $"{Code}, {Message}";
    }
}
