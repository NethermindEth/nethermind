// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;

namespace Nethermind.Runner.Monitoring.TransactionPool;

internal class TxPoolFlow
{
    public static ReadOnlyMemory<byte> NodeJson => _nodeJson;
    private static readonly byte[] _nodeJson = JsonSerializer.SerializeToUtf8Bytes(
           new Node[] {
                new (TxPoolStages.P2P, inclusion: true),
                new(TxPoolStages.ReceivedTxs, inclusion: true),
                new(TxPoolStages.NotSupportedTxType),
                new(TxPoolStages.TxTooLarge),
                new(TxPoolStages.GasLimitTooHigh),
                new(TxPoolStages.TooLowPriorityFee),
                new(TxPoolStages.TooLowFee),
                new(TxPoolStages.Malformed),
                new(TxPoolStages.NullHash),
                new(TxPoolStages.Duplicate),
                new(TxPoolStages.UnknownSender),
                new(TxPoolStages.ConflictingTxType),
                new(TxPoolStages.NonceTooFarInFuture),
                new(TxPoolStages.StateValidation, inclusion: true),
                new(TxPoolStages.ZeroBalance),
                new(TxPoolStages.BalanceLtTxValue),
                new(TxPoolStages.BalanceTooLow),
                new(TxPoolStages.NonceUsed),
                new(TxPoolStages.NoncesSkipped),
                new(TxPoolStages.ValidationSucceeded, inclusion: true),
                new(TxPoolStages.FailedReplacement),
                new(TxPoolStages.CannotCompete),
                new(TxPoolStages.TransactionPool, inclusion: true),
                new(TxPoolStages.Evicted),
                new(TxPoolStages.PrivateOrderFlow, inclusion: true),
                new(TxPoolStages.AddedToBlock, inclusion: true),
                new(TxPoolStages.ReorgedOut),
                new(TxPoolStages.ReorgedIn, inclusion: true),
           }, JsonSerializerOptions.Web);

    public Link[] Links { get; }

    public int PooledBlobTx { get; init; }
    public int PooledTx { get; init; }
    public long HashesReceived { get; internal set; }

    // Constructor that takes the metrics (TxPoolStages are fixed).
    public TxPoolFlow(
        long pendingTransactionsReceived,
        long pendingTransactionsNotSupportedTxType,
        long pendingTransactionsSizeTooLarge,
        long pendingTransactionsGasLimitTooHigh,
        long pendingTransactionsTooLowPriorityFee,
        long pendingTransactionsTooLowFee,
        long pendingTransactionsMalformed,
        long pendingTransactionsNullHash,
        long pendingTransactionsKnown,
        long pendingTransactionsUnresolvableSender,
        long pendingTransactionsConflictingTxType,
        long pendingTransactionsNonceTooFarInFuture,
        long pendingTransactionsZeroBalance,
        long pendingTransactionsBalanceBelowValue,
        long pendingTransactionsTooLowBalance,
        long pendingTransactionsLowNonce,
        long pendingTransactionsNonceGap,
        long pendingTransactionsPassedFiltersButCannotReplace,
        long pendingTransactionsPassedFiltersButCannotCompeteOnFees,
        long pendingTransactionsEvicted,
        long privateOrderFlow,
        long memPoolFlow,
        long reorged
    )
    {

        var stateValidation = pendingTransactionsReceived
                 - pendingTransactionsNotSupportedTxType
                 - pendingTransactionsSizeTooLarge
                 - pendingTransactionsGasLimitTooHigh
                 - pendingTransactionsTooLowPriorityFee
                 - pendingTransactionsTooLowFee
                 - pendingTransactionsMalformed
                 - pendingTransactionsNullHash
                 - pendingTransactionsKnown
                 - pendingTransactionsUnresolvableSender
                 - pendingTransactionsConflictingTxType
                 - pendingTransactionsNonceTooFarInFuture;
        var validationSuccess = stateValidation
                - pendingTransactionsZeroBalance
                - pendingTransactionsBalanceBelowValue
                - pendingTransactionsTooLowBalance
                - pendingTransactionsLowNonce
                - pendingTransactionsNonceGap;
        var addedToPool = validationSuccess
                - pendingTransactionsPassedFiltersButCannotReplace
                - pendingTransactionsPassedFiltersButCannotCompeteOnFees;

        Links = new[]
        {
            new Link(TxPoolStages.P2P, TxPoolStages.ReceivedTxs, pendingTransactionsReceived),
            new Link(TxPoolStages.ReceivedTxs, TxPoolStages.NotSupportedTxType, pendingTransactionsNotSupportedTxType),
            new Link(TxPoolStages.ReceivedTxs, TxPoolStages.TxTooLarge, pendingTransactionsSizeTooLarge),
            new Link(TxPoolStages.ReceivedTxs, TxPoolStages.GasLimitTooHigh, pendingTransactionsGasLimitTooHigh),
            new Link(TxPoolStages.ReceivedTxs, TxPoolStages.TooLowPriorityFee, pendingTransactionsTooLowPriorityFee),
            new Link(TxPoolStages.ReceivedTxs, TxPoolStages.TooLowFee, pendingTransactionsTooLowFee),
            new Link(TxPoolStages.ReceivedTxs, TxPoolStages.Malformed, pendingTransactionsMalformed),
            new Link(TxPoolStages.ReceivedTxs, TxPoolStages.NullHash, pendingTransactionsNullHash),
            new Link(TxPoolStages.ReceivedTxs, TxPoolStages.Duplicate, pendingTransactionsKnown),
            new Link(TxPoolStages.ReceivedTxs, TxPoolStages.UnknownSender, pendingTransactionsUnresolvableSender),
            new Link(TxPoolStages.ReceivedTxs, TxPoolStages.ConflictingTxType, pendingTransactionsConflictingTxType),
            new Link(TxPoolStages.ReceivedTxs, TxPoolStages.NonceTooFarInFuture, pendingTransactionsNonceTooFarInFuture),

            new Link(TxPoolStages.ReceivedTxs, TxPoolStages.StateValidation, stateValidation),

            new Link(TxPoolStages.StateValidation, TxPoolStages.ZeroBalance, pendingTransactionsZeroBalance),
            new Link(TxPoolStages.StateValidation, TxPoolStages.BalanceLtTxValue, pendingTransactionsBalanceBelowValue),
            new Link(TxPoolStages.StateValidation, TxPoolStages.BalanceTooLow, pendingTransactionsTooLowBalance),
            new Link(TxPoolStages.StateValidation, TxPoolStages.NonceUsed, pendingTransactionsLowNonce),
            new Link(TxPoolStages.StateValidation, TxPoolStages.NoncesSkipped, pendingTransactionsNonceGap),

            new Link(TxPoolStages.StateValidation, TxPoolStages.ValidationSucceeded, validationSuccess),

            new Link(TxPoolStages.ValidationSucceeded, TxPoolStages.FailedReplacement,
                pendingTransactionsPassedFiltersButCannotReplace),
            new Link(TxPoolStages.ValidationSucceeded, TxPoolStages.CannotCompete,
                pendingTransactionsPassedFiltersButCannotCompeteOnFees),

            new Link(TxPoolStages.ValidationSucceeded, TxPoolStages.TransactionPool, addedToPool),
            new Link(TxPoolStages.TransactionPool, TxPoolStages.Evicted, pendingTransactionsEvicted),
            new Link(TxPoolStages.TransactionPool, TxPoolStages.AddedToBlock, privateOrderFlow),
            new Link(TxPoolStages.PrivateOrderFlow, TxPoolStages.AddedToBlock, memPoolFlow),
            new Link(TxPoolStages.AddedToBlock, TxPoolStages.ReorgedOut, reorged),
            new Link(TxPoolStages.ReorgedIn, TxPoolStages.ReceivedTxs, reorged)
        };
    }
}
