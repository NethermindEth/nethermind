// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Eez.Execution.Settlement.RollupX;

/// <summary>Whole signed outbound groups followed by inbound deliveries in canonical Sync order.</summary>
internal sealed partial class NativeTransactions
{
    private readonly List<(ExecutionTable Table, byte[] User)> _outbound = [];
    private readonly List<L2ExecutionEntry> _inbound = [];

    private readonly ulong _chainId;

    private NativeTransactions(bool hasActions, ulong chainId)
    {
        HasActions = hasActions;
        _chainId = chainId;
    }

    public bool HasActions { get; }
    internal int GroupCount => checked(_outbound.Count + _inbound.Count);

    public static NativeTransactions Parse(StreamMessage[] messages, EezSettlementContext context)
    {
        NativeTransactions transactions = new(messages.Length != 0, context.ChainId);
        bool inbound = false;
        int index = 0;
        while (index < messages.Length)
        {
            if (messages[index++] is not StreamMessage.Initiate initiate) throw NativeCallTree.Invalid("missing transaction boundary");
            bool outbound = initiate.ChainId == context.RollupId;
            if (outbound && inbound || !outbound && initiate.ChainId != EezConstants.L1RollupId)
            {
                throw NativeCallTree.Invalid("foreign origin or outbound transaction after inbound phase");
            }
            inbound |= !outbound;
            NativeCallTree tree = NativeCallTree.Read(messages, ref index, initiate.ChainId, context.RollupId);
            NativeExecutionTables tables = new(tree, context.RollupId);
            if (outbound) transactions._outbound.Add((tables.Outbound(), UserTransaction(initiate.TxData, context.ChainId)));
            else transactions._inbound.AddRange(tables.Inbound());
        }

        return transactions;
    }

    /// <remarks>The native profile has one L1 host per signed bracket and no separate outbound callback deliveries.</remarks>
    public byte[][] Build(ProducingSlice applied, ulong systemNonce, EezSettlementContext context, Address eezl2)
    {
        if (applied.Skip < 0 || applied.Take < 0) throw NativeCallTree.Invalid("negative producing slice");
        int outboundSkip = Math.Min(applied.Skip, _outbound.Count);
        int outboundTake = Math.Min(applied.Take, _outbound.Count - outboundSkip);
        int inboundSkip = Math.Min(applied.Skip - outboundSkip, _inbound.Count);
        int inboundTake = Math.Min(Math.Max(applied.Take - outboundTake, 0), _inbound.Count - inboundSkip);
        ulong skipped = (ulong)(outboundSkip + inboundSkip);
        if (systemNonce > ulong.MaxValue - skipped) throw NativeCallTree.Invalid("system nonce overflow");
        ulong nonce = systemNonce + skipped;
        List<byte[]> transactions = new(2 * outboundTake + inboundTake);
        for (int i = outboundSkip; i < outboundSkip + outboundTake; i++)
        {
            (ExecutionTable table, byte[] user) = _outbound[i];
            transactions.Add(SyncBlock.Encode(context.ChainId, NextNonce(ref nonce), NativeValue.Funding(table.Entries),
                EezCalldata.EncodeLoadExecutionTable(table), eezl2));
            transactions.Add(user);
        }
        for (int i = inboundSkip; i < inboundSkip + inboundTake; i++)
        {
            L2ExecutionEntry[] entries = [_inbound[i]];
            transactions.Add(SyncBlock.Encode(context.ChainId, NextNonce(ref nonce), NativeValue.Funding(entries),
                EezCalldata.EncodeExecuteIncomingCrossChainCall(new IncomingCrossChainCall(entries, [])), eezl2));
        }

        return transactions.ToArray();
    }

    private static ulong NextNonce(ref ulong nonce) =>
        nonce == ulong.MaxValue ? throw NativeCallTree.Invalid("system nonce overflow") : nonce++;

    private static byte[] UserTransaction(ReadOnlyMemory<byte> encoded, ulong chainId)
    {
        Transaction transaction;
        try
        {
            RlpReader reader = new(encoded.Span);
            transaction = TxDecoder.Instance.Decode(ref reader, RlpBehaviors.ExcludeHashes | RlpBehaviors.SkipTypedWrapping)
                ?? throw NativeCallTree.Invalid("missing user transaction");
        }
        catch (RlpException e)
        {
            throw NativeCallTree.Invalid($"user transaction does not decode: {e.Message}");
        }

        byte[] canonical = TxDecoder.Instance.Encode(transaction, RlpBehaviors.SkipTypedWrapping).Bytes;
        if (!canonical.AsSpan().SequenceEqual(encoded.Span) || transaction.Type is TxType.Blob || transaction.Type == EezConstants.SystemTxType
            || transaction.ChainId is { } id && id != chainId)
        {
            throw NativeCallTree.Invalid($"user transaction is not canonical for chain {chainId}");
        }
        return canonical;
    }
}
