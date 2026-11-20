// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.TxPool;

namespace Nethermind.Runner.Ethereum.WebTransport;

internal sealed class WebTxHandler(ITxPool txPool)
{
    internal const int MaxRequestSize = 256 * 1024;
    private const int MaxSubmitCount = 16;

    internal byte[] Handle(ReadOnlySpan<byte> payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (payload.Length > MaxRequestSize)
        {
            throw new RlpLimitException("Browser transaction submission exceeds the message size limit.");
        }

        RlpReader reader = new(payload);
        RlpReader items = reader.EnterSequence(out int encodedLength);
        if (encodedLength != payload.Length)
        {
            throw new RlpReaderException("Browser transaction submission has trailing data.");
        }

        Transaction[] transactions = new Transaction[items.CountItems(MaxSubmitCount)];
        for (int i = 0; i < transactions.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            transactions[i] = DecodeTransaction(ref items);
        }

        // Decode the entire batch before admitting any of it. Pool rejections still produce one result per transaction.
        int contentLength = transactions.Length * Rlp.LengthOfSequence(1);
        byte[] response = new byte[Rlp.LengthOfSequence(contentLength)];
        RlpWriter writer = RlpWriter.ContentWriter(response);
        RlpSequence results = writer.StartSequence(contentLength);
        foreach (Transaction tx in transactions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TxResultCode result = tx.Type == TxType.Blob && tx.NetworkWrapper is null
                ? TxResultCode.InvalidTx
                : GetResultCode(txPool.SubmitTx(tx, TxHandlingOptions.RequireLocalAdmission));
            RlpSequence item = writer.StartSequence(1);
            writer.Write((byte)result);
            writer.EndSequence(item);
        }

        writer.EndSequence(results);
        return response;
    }

    private static Transaction DecodeTransaction(ref RlpReader items)
    {
        Transaction tx;
        if (items.IsNextSequence)
        {
            tx = TxDecoder.Instance.DecodeCompleteNotNull(items.ReadEncodedItem(), RlpBehaviors.SkipPooledTransactions);
        }
        else
        {
            ReadOnlySpan<byte> typed = items.ReadBytes<OnFlag>();
            if (typed.IsEmpty || (TxType)typed[0] is not (TxType.AccessList or TxType.EIP1559 or TxType.Blob or TxType.SetCode))
            {
                throw new RlpReaderException("Unsupported browser transaction type.");
            }

            RlpBehaviors behaviors = RlpBehaviors.SkipPooledTransactions | RlpBehaviors.SkipTypedWrapping;
            // A type-3 network wrapper starts with the transaction list. A canonical type-3 envelope starts with its chain ID.
            if (typed[0] == (byte)TxType.Blob && new RlpReader(typed[1..]).EnterSequence(out _).IsNextSequence)
            {
                behaviors |= RlpBehaviors.InMempoolForm;
            }
            tx = TxDecoder.Instance.DecodeCompleteNotNull(typed, behaviors);
        }

        if (tx.Signature is null)
        {
            throw new RlpReaderException("Browser transaction is missing its signature.");
        }

        return tx;
    }

    private static TxResultCode GetResultCode(AcceptTxResult result) => result switch
    {
        _ when result == AcceptTxResult.Accepted => TxResultCode.Accepted,
        _ when result == AcceptTxResult.AlreadyKnown => TxResultCode.AlreadyKnown,
        _ when result == AcceptTxResult.OldNonce || result == AcceptTxResult.NonceGap
            || result == AcceptTxResult.NonceTooFarInFuture => TxResultCode.InvalidNonce,
        _ when result == AcceptTxResult.ReplacementNotAllowed => TxResultCode.InvalidFee,
        _ when result == AcceptTxResult.FeeTooLow => TxResultCode.GasPriceTooLow,
        _ when result == AcceptTxResult.InsufficientFunds => TxResultCode.InsufficientFunds,
        _ when result == AcceptTxResult.GasLimitExceeded => TxResultCode.InvalidGasLimit,
        _ when result == AcceptTxResult.PoolUnavailable || result == AcceptTxResult.Syncing || result == AcceptTxResult.FeeTooLowToCompete
            || result == AcceptTxResult.PendingTxsOfConflictingType || result == AcceptTxResult.NotCurrentNonceForDelegation
            || result == AcceptTxResult.DelegatorHasPendingTx => TxResultCode.PoolError,
        _ => TxResultCode.InvalidTx
    };

    private enum TxResultCode : byte
    {
        Accepted = 0,
        AlreadyKnown = 1,
        InvalidNonce = 2,
        InvalidFee = 3,
        GasPriceTooLow = 4,
        InsufficientFunds = 5,
        InvalidGasLimit = 6,
        PoolError = 7,
        InvalidTx = 8
    }
}
