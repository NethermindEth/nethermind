// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core.Messages;

namespace Nethermind.TxPool
{
    /// <summary>
    /// Describes potential outcomes of adding transaction to the TX pool.
    /// </summary>
    /// <remarks>
    /// Equality is by an id that the declarations below hand out in order, never by a literal, so results added
    /// independently on parallel branches cannot end up sharing one. Ids start at zero, so <see cref="Accepted"/>,
    /// declared first, keeps id 0 and a default-valued result still reads as accepted.
    /// </remarks>
    public readonly struct AcceptTxResult : IEquatable<AcceptTxResult>
    {
        // Deliberately uninitialized: an initializer would make the ids depend on where this field sits in the file.
        private static int _lastId;

        /// <summary>
        /// The transaction has been accepted. This is the only 'success' outcome.
        /// </summary>
        // Code intentionally kept as nameof(): the success path returns the tx hash, not this Code string.
        public static readonly AcceptTxResult Accepted = new(nameof(Accepted));

        /// <summary>
        /// A transaction with the same hash has already been added to the pool in the past.
        /// </summary>
        public static readonly AcceptTxResult AlreadyKnown = new(TxPoolErrorMessages.AlreadyKnown);

        /// <summary>
        /// Covers scenarios where sender recovery fails.
        /// </summary>
        public static readonly AcceptTxResult FailedToResolveSender = new(TxPoolErrorMessages.FailedToRecoverSender);

        /// <summary>
        /// Fee paid by this transaction is not enough to be accepted in the mempool.
        /// </summary>
        public static readonly AcceptTxResult FeeTooLow = new(TxPoolErrorMessages.TransactionUnderpriced);

        /// <summary>
        /// Fee paid by this transaction is not enough to be accepted in the mempool.
        /// </summary>
        public static readonly AcceptTxResult FeeTooLowToCompete = new(TxPoolErrorMessages.TransactionUnderpriced);

        /// <summary>
        /// Transaction gas limit exceeds the block gas limit.
        /// </summary>
        public static readonly AcceptTxResult GasLimitExceeded = new(TxPoolErrorMessages.GasLimitReached);

        /// <summary>
        /// Sender account has not enough balance to execute this transaction.
        /// </summary>
        public static readonly AcceptTxResult InsufficientFunds = new(TxErrorMessages.InsufficientFundsForGas);

        /// <summary>
        /// Calculation of gas price * gas limit + value overflowed int256.
        /// </summary>
        public static readonly AcceptTxResult Int256Overflow = new(TxPoolErrorMessages.TransactionOverflow);

        /// <summary>
        /// Transaction format is invalid.
        /// </summary>
        public static readonly AcceptTxResult Invalid = new(TxPoolErrorMessages.TransactionInvalid);

        /// <summary>
        /// The nonce is not the next nonce after the last nonce of this sender present in TxPool.
        /// </summary>
        public static readonly AcceptTxResult NonceGap = new(TxPoolErrorMessages.NonceTooHigh);

        /// <summary>
        /// The EOA (externally owned account) that signed this transaction (sender) has already signed and executed a transaction with the same nonce.
        /// </summary>
        public static readonly AcceptTxResult OldNonce = new(TxPoolErrorMessages.NonceTooLow);

        /// <summary>
        /// Transaction is not allowed to replace the one already in the pool. Fee bump is too low or some requirements are not fulfilled
        /// </summary>
        public static readonly AcceptTxResult ReplacementNotAllowed = new(TxPoolErrorMessages.ReplacementTransactionUnderpriced);

        /// <summary>
        /// Transaction sender has code hash that is not null.
        /// </summary>
        public static readonly AcceptTxResult SenderIsContract = new(TxPoolErrorMessages.SenderNotEoa);

        /// <summary>
        /// The nonce is too far in the future.
        /// </summary>
        public static readonly AcceptTxResult NonceTooFarInFuture = new(TxPoolErrorMessages.NonceTooFarInFuture);

        /// <summary>
        /// Ignores blob transactions if sender already have pending transactions of other types; ignore other types if has already pending blobs
        /// </summary>
        public static readonly AcceptTxResult PendingTxsOfConflictingType = new(TxPoolErrorMessages.PendingTransactionTypeConflict);

        /// <summary>
        /// Ignores transactions if tx type is not supported
        /// </summary>
        public static readonly AcceptTxResult NotSupportedTxType = new(TxPoolErrorMessages.UnsupportedTransactionType);

        /// <summary>
        /// Transaction size exceeds configured max size.
        /// </summary>
        public static readonly AcceptTxResult MaxTxSizeExceeded = new(TxPoolErrorMessages.TransactionTooLarge);

        /// <summary>
        /// Only one tx with current state matching nonce is allowed per delegated account or pending delegation.
        /// </summary>
        public static readonly AcceptTxResult NotCurrentNonceForDelegation = new(TxPoolErrorMessages.DelegationNonceGap);

        /// <summary>
        /// There is a pending transaction from a delegation in the tx pool already.
        /// </summary>
        public static readonly AcceptTxResult DelegatorHasPendingTx = new(TxPoolErrorMessages.DelegationAuthorityHasPendingTx);

        /// <summary>
        /// The node is syncing and cannot accept transactions at this time.
        /// </summary>
        public static readonly AcceptTxResult Syncing = new(TxPoolErrorMessages.NodeIsSyncing);

        /// <summary>
        /// The signer could not produce a signature for the transaction (locked account, missing key, remote signer rejection).
        /// </summary>
        // Code intentionally kept as nameof(): EthRpcModule.SendTx intercepts this result and surfaces only the
        // Message field with ErrorCodes.AccountLocked (-32020), so the Code string never reaches RPC callers.
        public static readonly AcceptTxResult SignFailed = new(nameof(SignFailed), "authentication needed: password or unlock");

        /// <summary>
        /// An EIP-8141 frame transaction whose expiry-verifier deadline is already behind the current head; it can
        /// never be included, so it must not enter the pool or be broadcast.
        /// </summary>
        public static readonly AcceptTxResult FrameTxExpired = new(TxPoolErrorMessages.FrameTxExpired);

        /// <summary>
        /// An EIP-8141 frame transaction whose validation prefix plus signature verification would cost a node more
        /// than <c>MAX_VERIFY_GAS</c> to check. It stays consensus-valid; only public mempool propagation is refused.
        /// </summary>
        public static readonly AcceptTxResult FrameTxVerifyGasTooHigh = new(TxPoolErrorMessages.FrameTxVerifyGasTooHigh);

        /// <summary>
        /// An EIP-8141 frame transaction whose validation prefix budgets more state gas than <c>MAX_VERIFY_STATE_GAS</c>.
        /// A separate mempool bound from <see cref="FrameTxVerifyGasTooHigh"/>; it too refuses only propagation, not validity.
        /// </summary>
        public static readonly AcceptTxResult FrameTxVerifyStateGasTooHigh = new(TxPoolErrorMessages.FrameTxVerifyStateGasTooHigh);

        /// <summary>
        /// An EIP-8250 transaction whose selected nonce keys are not all at its <c>nonce_seq</c> in the head state.
        /// Unlike an account nonce this is an exact match in both directions, so the transaction is neither old nor future.
        /// </summary>
        public static readonly AcceptTxResult KeyedNonceUnmet = new(TxPoolErrorMessages.KeyedNonceUnmet);

        /// <summary>
        /// An EIP-8141 frame transaction whose resolved payer's summed pending maximum cost would exceed the payer's balance.
        /// </summary>
        public static readonly AcceptTxResult FrameTxPayerExposureExceeded = new(TxPoolErrorMessages.FrameTxPayerExposureExceeded);

        /// <summary>
        /// An EIP-8141 frame transaction whose validation prefix can never approve a payer.
        /// </summary>
        /// <remarks>Unincludable rather than malformed, so it must not disconnect the peer that relayed it.</remarks>
        public static readonly AcceptTxResult FrameTxNoPayer = new(TxPoolErrorMessages.FrameTxNoPayer);

        /// <summary>
        /// An EIP-8141 blob-carrying frame transaction submitted without the blob sidecar that its mempool form requires.
        /// </summary>
        public static readonly AcceptTxResult FrameTxMissingSidecar = new(TxPoolErrorMessages.FrameTxMissingSidecar);

        /// <summary>
        /// An EIP-8141 frame transaction carrying a <c>VERIFY</c> frame behind its validation prefix, whose revert
        /// would invalidate the transaction on state the pool never validated. It stays consensus-valid; only public
        /// mempool propagation is refused.
        /// </summary>
        public static readonly AcceptTxResult FrameTxVerifyAfterPrefix = new(TxPoolErrorMessages.FrameTxVerifyAfterPrefix);

        /// <summary>
        /// An EIP-8141 frame transaction whose expiry verifier frame does not lead its frame list, the only placement
        /// the spec permits. It stays consensus-valid; only public mempool propagation is refused.
        /// </summary>
        public static readonly AcceptTxResult FrameTxMisplacedExpiryFrame = new(TxPoolErrorMessages.FrameTxMisplacedExpiryFrame);

        /// <summary>
        /// An EIP-8141 frame transaction whose opaque validation prefix failed in-pool simulation.
        /// </summary>
        public static readonly AcceptTxResult FrameSimulationFailed = new(TxPoolErrorMessages.FrameSimulationFailed);

        /// <summary>
        /// Declares a result distinct from every other declared result.
        /// </summary>
        /// <remarks>For static declarations only: every call permanently consumes an id from a process-wide
        /// counter, so two calls with identical arguments are not equal to each other.</remarks>
        /// <param name="code">The short code reported to the submitter.</param>
        /// <param name="message">An optional detail appended to <paramref name="code"/>.</param>
        public AcceptTxResult(string code, string? message = null)
            : this(Interlocked.Increment(ref _lastId) - 1, code, message)
        {
        }

        private AcceptTxResult(int id, string code, string? message)
        {
            Id = id;
            Code = code;
            Message = message;
        }

        private int Id { get; }
        private string Code { get; }
        private string? Message { get; }

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
