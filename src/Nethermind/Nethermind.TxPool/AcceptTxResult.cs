// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Messages;

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
        // Code intentionally kept as nameof(): the success path returns the tx hash, not this Code string.
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
        public static readonly AcceptTxResult FeeTooLowToCompete = new(4, TxPoolErrorMessages.TransactionUnderpriced);

        /// <summary>
        /// Transaction gas limit exceeds the block gas limit.
        /// </summary>
        public static readonly AcceptTxResult GasLimitExceeded = new(5, TxPoolErrorMessages.GasLimitReached);

        /// <summary>
        /// Sender account has not enough balance to execute this transaction.
        /// </summary>
        public static readonly AcceptTxResult InsufficientFunds = new(6, TxErrorMessages.InsufficientFundsForGas);

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
        /// Blob or cell proofs failed cryptographic validation after cheaper admission checks passed.
        /// </summary>
        public static readonly AcceptTxResult InvalidBlobProofs = new(20, TxErrorMessages.InvalidBlobProofs);

        /// <summary>
        /// The blob transaction sidecar does not contain full blobs or any sparse cells.
        /// </summary>
        public static readonly AcceptTxResult IncompleteBlobData = new(21, TxErrorMessages.IncompleteBlobData);

        /// <summary>
        /// The node is syncing and cannot accept transactions at this time.
        /// </summary>
        public static readonly AcceptTxResult Syncing = new(503, TxPoolErrorMessages.NodeIsSyncing);

        /// <summary>
        /// The signer could not produce a signature for the transaction (locked account, missing key, remote signer rejection).
        /// </summary>
        // Code intentionally kept as nameof(): EthRpcModule.SendTx intercepts this result and surfaces only the
        // Message field with ErrorCodes.AccountLocked (-32020), so the Code string never reaches RPC callers.
        public static readonly AcceptTxResult SignFailed = new(19, nameof(SignFailed), "authentication needed: password or unlock");

        // Ids 22-33 belong to the results below; 0-21 and 503 are the pre-existing ones. Equality is by id
        // alone, so a new result must take a free id; AcceptTxResultTests guards that they stay unique.

        /// <summary>
        /// An EIP-8141 frame transaction whose expiry-verifier deadline is already behind the current head; it can
        /// never be included, so it must not enter the pool or be broadcast.
        /// </summary>
        public static readonly AcceptTxResult FrameTxExpired = new(30, TxPoolErrorMessages.FrameTxExpired);

        /// <summary>
        /// An EIP-8141 frame transaction whose validation prefix plus signature verification would cost a node more
        /// than <c>MAX_VERIFY_GAS</c> to check. It stays consensus-valid; only public mempool propagation is refused.
        /// </summary>
        public static readonly AcceptTxResult FrameTxVerifyGasTooHigh = new(31, TxPoolErrorMessages.FrameTxVerifyGasTooHigh);

        /// <summary>An EIP-8141 frame transaction whose validation prefix budgets more state gas than <c>MAX_VERIFY_STATE_GAS</c>. A propagation bound separate from <see cref="FrameTxVerifyGasTooHigh"/>, not a validity rule.</summary>
        public static readonly AcceptTxResult FrameTxVerifyStateGasTooHigh = new(29, TxPoolErrorMessages.FrameTxVerifyStateGasTooHigh);

        /// <summary>An EIP-8250 transaction whose selected nonce keys are not all at its <c>nonce_seq</c>: an exact match, so neither old nor future.</summary>
        public static readonly AcceptTxResult KeyedNonceUnmet = new(24, TxPoolErrorMessages.KeyedNonceUnmet);

        /// <summary>An EIP-8141 frame transaction whose resolved payer's summed pending maximum cost would exceed the payer's balance.</summary>
        public static readonly AcceptTxResult FrameTxPayerExposureExceeded = new(22, TxPoolErrorMessages.FrameTxPayerExposureExceeded);

        /// <summary>An EIP-8141 frame transaction whose validation prefix can never approve a payer: unincludable rather than malformed, so the relaying peer is not disconnected.</summary>
        public static readonly AcceptTxResult FrameTxNoPayer = new(23, TxPoolErrorMessages.FrameTxNoPayer);

        /// <summary>An EIP-8141 blob-carrying frame transaction submitted without the blob sidecar that its mempool form requires.</summary>
        // Equality is by id alone, and KeyedNonceUnmet holds 24.
        public static readonly AcceptTxResult FrameTxMissingSidecar = new(27, TxPoolErrorMessages.FrameTxMissingSidecar);

        /// <summary>An EIP-8141 frame transaction with a <c>VERIFY</c> frame behind its validation prefix. A propagation bound, not a validity rule.</summary>
        public static readonly AcceptTxResult FrameTxVerifyAfterPrefix = new(25, TxPoolErrorMessages.FrameTxVerifyAfterPrefix);

        /// <summary>An EIP-8141 frame transaction whose expiry verifier frame does not lead its frame list. A propagation bound, not a validity rule.</summary>
        public static readonly AcceptTxResult FrameTxMisplacedExpiryFrame = new(26, TxPoolErrorMessages.FrameTxMisplacedExpiryFrame);

        /// <summary>An EIP-8141 frame transaction whose opaque validation prefix failed in-pool simulation.</summary>
        public static readonly AcceptTxResult FrameSimulationFailed = new(28, TxPoolErrorMessages.FrameSimulationFailed);

        /// <summary>
        /// An EIP-8141 frame transaction paying through an already fully-committed non-canonical paymaster.
        /// </summary>
        public static readonly AcceptTxResult NonCanonicalPaymasterLimitReached = new(32, TxPoolErrorMessages.NonCanonicalPaymasterLimitReached);

        /// <summary>
        /// The node declined to simulate an EIP-8141 validation prefix because its own admission bounds were
        /// spent, so the transaction was never judged.
        /// </summary>
        /// <remarks>
        /// Distinct from <see cref="FrameSimulationFailed"/> so peer scoring can tell load shedding apart from
        /// a peer sending transactions this node rejects.
        /// </remarks>
        public static readonly AcceptTxResult FrameSimulationDeferred = new(33, TxPoolErrorMessages.FrameSimulationDeferred);

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
