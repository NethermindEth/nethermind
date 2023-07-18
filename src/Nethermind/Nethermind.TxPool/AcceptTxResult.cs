// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.TxPool
{
    /// <summary>
    /// Describes potential outcomes of adding transaction to the TX pool.
    /// </summary>
    public readonly struct AcceptTxResult : IEquatable<AcceptTxResult>
    {
        /// <summary>
        /// The transaction has been accepted. This is the only 'success' outcome.
        /// </summary>
        public static readonly AcceptTxResult Accepted = new(0, nameof(Accepted));

        /// <summary>
        /// A transaction with the same hash has already been added to the pool in the past.
        /// </summary>
        public static readonly AcceptTxResult AlreadyKnown = new(1, nameof(AlreadyKnown));

        /// <summary>
        /// Covers scenarios where sender recovery fails.
        /// </summary>
        public static readonly AcceptTxResult FailedToResolveSender = new(2, nameof(FailedToResolveSender));

        /// <summary>
        /// Fee paid by this transaction is not enough to be accepted in the mempool.
        /// </summary>
        public static readonly AcceptTxResult FeeTooLow = new(3, nameof(FeeTooLow));

        /// <summary>
        /// Fee paid by this transaction is not enough to be accepted in the mempool.
        /// </summary>
        public static readonly AcceptTxResult FeeTooLowToCompete = new(4, nameof(FeeTooLowToCompete));

        /// <summary>
        /// Transaction gas limit exceeds the block gas limit.
        /// </summary>
        public static readonly AcceptTxResult GasLimitExceeded = new(5, nameof(GasLimitExceeded));

        /// <summary>
        /// Sender account has not enough balance to execute this transaction.
        /// </summary>
        public static readonly AcceptTxResult InsufficientFunds = new(6, nameof(InsufficientFunds));

        /// <summary>
        /// Calculation of gas price * gas limit + value overflowed int256.
        /// </summary>
        public static readonly AcceptTxResult Int256Overflow = new(7, nameof(Int256Overflow));

        /// <summary>
        /// Transaction format is invalid.
        /// </summary>
        public static readonly AcceptTxResult Invalid = new(8, nameof(Invalid));

        /// <summary>
        /// The nonce is not the next nonce after the last nonce of this sender present in TxPool.
        /// </summary>
        public static readonly AcceptTxResult NonceGap = new(9, nameof(NonceGap));

        /// <summary>
        /// The EOA (externally owned account) that signed this transaction (sender) has already signed and executed a transaction with the same nonce.
        /// </summary>
        public static readonly AcceptTxResult OldNonce = new(10, nameof(OldNonce));

        /// <summary>
        /// A transaction with same nonce has been signed locally already and is awaiting in the pool.
        /// (I would like to change this behaviour to allow local replacement)
        /// </summary>
        public static readonly AcceptTxResult OwnNonceAlreadyUsed = new(11, nameof(OwnNonceAlreadyUsed));

        /// <summary>
        /// Transaction sender has code hash that is not null.
        /// </summary>
        public static readonly AcceptTxResult SenderIsContract = new(12, nameof(SenderIsContract));

        /// <summary>
        /// Transaction is not allowed to replace the one already in the pool. Fee bump is too low or some requirements are not fulfilled
        /// </summary>
        public static readonly AcceptTxResult ReplacementNotAllowed = new(13, nameof(ReplacementNotAllowed));

        /// <summary>
        /// Ignores blob transactions if sender already have pending transactions of other types; ignore other types if has already pending blobs
        /// </summary>
        public static readonly AcceptTxResult PendingTxsOfOtherType = new(14, nameof(PendingTxsOfOtherType));

        private int Id { get; }
        private string Code { get; }
        private string? Message { get; }

        public AcceptTxResult(int id, string code, string? message = null)
        {
            Id = id;
            Code = code;
            Message = message;
        }

        public static implicit operator bool(AcceptTxResult result) => result.Id == Accepted.Id;
        public AcceptTxResult WithMessage(string message) => new(Id, Code, message);
        public static bool operator ==(AcceptTxResult a, AcceptTxResult b) => a.Equals(b);
        public static bool operator !=(AcceptTxResult a, AcceptTxResult b) => !(a == b);
        public override bool Equals(object? obj) => obj is AcceptTxResult result && Equals(result);
        public bool Equals(AcceptTxResult result) => Id == result.Id;
        public override int GetHashCode() => Id.GetHashCode();
        public override string ToString() => Message is null ? $"{Code}" : $"{Code}, {Message}";
    }
}
