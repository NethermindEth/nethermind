// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Network.Enr;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.TxPool;

namespace Nethermind.Runner.Ethereum.WebTransport;

internal sealed class BrowserProtocol(IEthereumEcdsa ecdsa, IProtectedPrivateKey nodeKey, ITxPool txPool) : IDisposable
{
    internal const byte TxMode = 1;
    internal const byte DiscoveryMode = 2;
    internal const int NonceLength = 32;
    internal const int MaxSubmitSize = 256 * 1024;
    internal const int MaxPeerRecords = 16;
    private const int MaxSubmitCount = 16;
    private const int MaxRecordSize = 300;
    private readonly PrivateKey _privateKey = nodeKey.Unprotect();

    public void Dispose() => _privateKey.Dispose();

    internal byte[] CreateHandshake(byte mode, ReadOnlySpan<byte> nonce)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(nonce.Length, NonceLength);
        ReadOnlySpan<byte> capability = mode switch
        {
            TxMode => "webtx/1"u8,
            DiscoveryMode => "webdisc/1"u8,
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };

        // Geth uses the label and raw nonce as the signature preimage, independent of the CONNECT header encoding.
        ReadOnlySpan<byte> label = "ENR-key-proof-v1"u8;
        Span<byte> challenge = stackalloc byte[label.Length + NonceLength];
        label.CopyTo(challenge);
        nonce.CopyTo(challenge[label.Length..]);
        ValueHash256 digest = ValueKeccak.Compute(challenge);
        using Signature signature = ecdsa.Sign(_privateKey, in digest);
        Span<byte> proof = stackalloc byte[Signature.Size];
        signature.WriteBytesWithRecoveryTo(proof);

        int capabilityLength = Rlp.LengthOf(capability);
        int contentLength = Rlp.LengthOf(proof) + Rlp.LengthOfSequence(capabilityLength);
        byte[] handshake = new byte[Rlp.LengthOfSequence(contentLength)];
        RlpWriter writer = RlpWriter.ContentWriter(handshake);
        RlpSequence outer = writer.StartSequence(contentLength);
        writer.Write(proof);
        RlpSequence capabilities = writer.StartSequence(capabilityLength);
        writer.Write(capability);
        writer.EndSequence(capabilities);
        writer.EndSequence(outer);
        return handshake;
    }

    internal byte[] Submit(ReadOnlySpan<byte> payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (payload.Length > MaxSubmitSize)
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
                : GetResultCode(txPool.SubmitTx(tx, TxHandlingOptions.None));
            RlpSequence item = writer.StartSequence(1);
            writer.Write((byte)result);
            writer.EndSequence(item);
        }

        writer.EndSequence(results);
        return response;
    }

    internal static byte[] EncodePeerRecords(IReadOnlyList<NodeRecord> records)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(records.Count, MaxPeerRecords);
        int contentLength = 0;
        foreach (NodeRecord record in records)
        {
            int recordLength = record.GetRlpLengthWithSignature();
            if (record.Signature is null || recordLength > MaxRecordSize)
            {
                throw new ArgumentException("Browser discovery requires signed EIP-778 node records.", nameof(records));
            }

            contentLength += recordLength;
        }

        byte[] response = new byte[Rlp.LengthOfSequence(contentLength)];
        RlpWriter writer = RlpWriter.ContentWriter(response);
        RlpSequence list = writer.StartSequence(contentLength);
        foreach (NodeRecord record in records)
        {
            record.Encode(ref writer);
        }

        writer.EndSequence(list);
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
            if (typed.IsEmpty || typed[0] is 0 or > (byte)TxType.SetCode)
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
        _ when result == AcceptTxResult.Syncing || result == AcceptTxResult.FeeTooLowToCompete
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
