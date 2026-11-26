// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Serialization.FluentRlp;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Serialization.Rlp;

/// <summary>Encodes and decodes block bodies.</summary>
/// <remarks>
/// The standard decode entry point rents transactions unless <see cref="RlpBehaviors.SkipPooledTransactions"/>
/// is specified. Retained blocks must opt out; pooled transactions require an exclusive owner and a return path.
/// </remarks>
[method: DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(BlockBodyDecoder))]
public sealed class BlockBodyDecoder(IHeaderDecoder? headerDecoder = null) : RlpDecoder<BlockBody>
{
    public static RlpLimit TransactionsCountLimit => RlpLimit.For<BlockBody>(
        checked((int)(RlpLimit.MaxBlockGas / GasCostOf.TransactionEip2780 + 1)),
        nameof(BlockBody.Transactions)
    );

    public static readonly RlpLimit UnclesCountLimit = RlpLimit.For<BlockBody>(2, nameof(BlockBody.Uncles));

    // Actual consensus-level max is 16, see MAX_WITHDRAWALS_PER_PAYLOAD at https://github.com/ethereum/consensus-specs/blob/master/specs/capella/beacon-chain.md
    // Increased here for compatibility with execution spec tests and benchmarks
    public static readonly RlpLimit WithdrawalsCountLimit = RlpLimit.For<BlockBody>(64_000, nameof(BlockBody.Withdrawals));

    private readonly TxDecoder _txDecoder = TxDecoder.Instance;
    private readonly IHeaderDecoder _headerDecoder = headerDecoder ?? new HeaderDecoder();
    private readonly WithdrawalDecoder _withdrawalDecoderDecoder = new();

    private static BlockBodyDecoder? _instance;
    public static BlockBodyDecoder Instance => _instance ??= new BlockBodyDecoder();

    public override int GetLength(BlockBody? item, RlpBehaviors rlpBehaviors)
        => item is null ? Rlp.LengthOfNull : Rlp.LengthOfSequence(GetBodyLength(item));

    public int GetBodyLength(BlockBody b) => GetBodyLength(GetBodyComponentLength(b));

    private static int GetBodyLength((int Txs, int Uncles, int? Withdrawals) lengths) =>
        Rlp.LengthOfSequence(lengths.Txs) +
        Rlp.LengthOfSequence(lengths.Uncles) +
        (lengths.Withdrawals is not null ? Rlp.LengthOfSequence(lengths.Withdrawals.Value) : 0);

    public (int Txs, int Uncles, int? Withdrawals) GetBodyComponentLength(BlockBody b) =>
        GetBodyComponentLength(b, GetTxLength(b.Transactions));

    internal (int Txs, int Uncles, int? Withdrawals) GetBodyComponentLength(BlockBody b, int transactionLength) =>
    (
        transactionLength,
        GetUnclesLength(b.Uncles),
        b.Withdrawals is not null ? GetWithdrawalsLength(b.Withdrawals) : null
    );

    private int GetTxLength(Transaction[] transactions)
    {
        if (transactions.Length == 0) return 0;

        int sum = 0;
        foreach (Transaction tx in transactions)
        {
            sum += _txDecoder.GetLength(tx, RlpBehaviors.None);
        }

        return sum;
    }

    private int GetUnclesLength(BlockHeader[] headers)
    {
        if (headers.Length == 0) return 0;

        int sum = 0;
        foreach (BlockHeader header in headers)
        {
            sum += _headerDecoder.GetLength(header, RlpBehaviors.None);
        }

        return sum;
    }

    private int GetWithdrawalsLength(Withdrawal[] withdrawals)
    {
        if (withdrawals.Length == 0) return 0;

        int sum = 0;
        foreach (Withdrawal withdrawal in withdrawals)
        {
            sum += _withdrawalDecoderDecoder.GetLength(withdrawal, RlpBehaviors.None);
        }

        return sum;
    }

    protected override BlockBody? DecodeInternal(ref RlpReader reader, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        => rlpBehaviors.IsTrustedInput() ? Decode<OffFlag>(ref reader, rlpBehaviors) : Decode<OnFlag>(ref reader, rlpBehaviors);

    private BlockBody? Decode<TCanonicalChecks>(ref RlpReader reader, RlpBehaviors rlpBehaviors) where TCanonicalChecks : struct, IFlag
    {
        RlpReader body = reader.EnterSequence<TCanonicalChecks>(out int itemLength);
        BlockBody? result = body.HasNext
            ? DecodeUnwrapped<TCanonicalChecks>(ref body, rlpBehaviors, usePooledTransactions: (rlpBehaviors & RlpBehaviors.SkipPooledTransactions) == 0)
            : null;
        reader.ExitSequence(in body, itemLength);
        return result;
    }

    /// <summary>Decodes body contents without the outer sequence prefix, with optional transaction pooling.</summary>
    /// <param name="reader">Reader over the body contents, from the transaction sequence to the end of the body.</param>
    /// <param name="usePooledTransactions">
    /// Rent exclusively owned transactions. Return them to <see cref="TxDecoder.TxObjectPool"/> at most once,
    /// after all consumers finish. Pass false for retained blocks.
    /// </param>
    /// <param name="rlpBehaviors">Only <see cref="RlpBehaviors.TrustedInput"/> applies, also to the nested items.</param>
    /// <remarks>The JSON-RPC <c>HashesOnlyBlockReader</c> reads the same layout without this decoder and leaves any block
    /// it does not recognise to it; a body field added here needs adding there to keep that fast path.</remarks>
    public BlockBody DecodeUnwrapped(ref RlpReader reader, bool usePooledTransactions, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        => rlpBehaviors.IsTrustedInput()
            ? DecodeUnwrapped<OffFlag>(ref reader, rlpBehaviors, usePooledTransactions)
            : DecodeUnwrapped<OnFlag>(ref reader, rlpBehaviors, usePooledTransactions);

    internal BlockBody DecodeUnwrapped<TCanonicalChecks>(ref RlpReader reader, RlpBehaviors rlpBehaviors, bool usePooledTransactions) where TCanonicalChecks : struct, IFlag
    {
        // The nested items get only the input mode, so that the other behaviors apply to this item only.
        RlpBehaviors itemBehaviors = rlpBehaviors & RlpBehaviors.TrustedInput;
        Transaction[] transactions = DecodeTransactions<TCanonicalChecks>(ref reader, itemBehaviors, usePooledTransactions);
        try
        {
            BlockHeader[] uncles = _headerDecoder.DecodeNonNullArray(ref reader, itemBehaviors, UnclesCountLimit);
            Withdrawal[]? withdrawals = null;

            if (reader.HasNext)
            {
                withdrawals = DecodeWithdrawals<TCanonicalChecks>(ref reader, itemBehaviors);
            }

            reader.CheckEnd();
            return new BlockBody(transactions, uncles, withdrawals);
        }
        catch
        {
            if (usePooledTransactions)
            {
                foreach (Transaction transaction in transactions)
                    TxDecoder.TxObjectPool.Return(transaction);
            }
            throw;
        }
    }

    private Transaction[] DecodeTransactions<TCanonicalChecks>(ref RlpReader reader, RlpBehaviors itemBehaviors, bool usePooledTransactions) where TCanonicalChecks : struct, IFlag
    {
        RlpReader items = reader.EnterSequence<TCanonicalChecks>(out int itemLength);
        int count = items.CountItems<TCanonicalChecks>(TransactionsCountLimit);
        Transaction[] transactions = new Transaction[count];
        int decoded = 0;
        try
        {
            for (int i = 0; i < count; i++)
            {
                if (items.IsNextItemEmptyList()) ThrowNullArrayElement(i);
                Transaction? transaction = usePooledTransactions ? TxDecoder.TxObjectPool.Get() : new Transaction();
                transactions[decoded++] = transaction;
                _txDecoder.Decode(ref items, ref transaction, itemBehaviors);
                if (transaction is null) ThrowNullArrayElement(i);
                if (usePooledTransactions && !ReferenceEquals(transaction, transactions[i]))
                    TxDecoder.TxObjectPool.Return(transactions[i]);
                transactions[i] = transaction;
            }
            reader.ExitSequence(in items, itemLength);
            return transactions;
        }
        catch
        {
            if (usePooledTransactions)
            {
                for (int i = 0; i < decoded; i++) TxDecoder.TxObjectPool.Return(transactions[i]);
            }
            throw;
        }
    }

    /// <summary>Decodes a body, but keeps each transaction in its encoded form.</summary>
    /// <remarks>
    /// <para>
    /// Each transaction is a pooled <see cref="Transaction"/> that holds only its type and, as its pre-hash, the bytes
    /// that the transaction trie stores. This is enough for the transaction root and the transaction hash, and saves
    /// decoding transactions that are stored but not executed. Return the transactions to
    /// <see cref="TxDecoder.TxObjectPool"/> before <paramref name="body"/> is released, and never execute or encode them.
    /// </para>
    /// <para>
    /// The fields inside a transaction are not checked, so compare the transaction root with the header before the
    /// body is trusted. All other items get the checks of the full decode, and the uncles must be in the encoding
    /// that <see cref="IHeaderDecoder"/> writes: the uncles hash is computed from that encoding, so a body kept as
    /// received is covered by it only then.
    /// </para>
    /// <para>
    /// The first read of a transaction's <see cref="Transaction.Hash"/> drops its encoding, so compute the transaction
    /// root before reading any hash.
    /// </para>
    /// </remarks>
    /// <param name="body">The encoded body with its sequence prefix. The transactions refer to these bytes.</param>
    /// <returns>The body, or <see langword="null"/> for an empty sequence, which marks a missing body.</returns>
    public BlockBody? DecodeWithEncodedTransactions(Memory<byte> body)
    {
        RlpReader reader = new(body.Span);
        RlpReader content = reader.EnterSequence(out int itemLength);
        BlockBody? result = null;
        if (content.HasNext)
        {
            Memory<byte> contentSource = body[(itemLength - content.Remaining)..];
            Transaction[] transactions = ReadEncodedTransactions(ref content, contentSource);
            try
            {
                int unclesStart = content.BytesRead;
                BlockHeader[] uncles = _headerDecoder.DecodeNonNullArray(ref content, RlpBehaviors.None, UnclesCountLimit);
                CheckCanonicalUnclesItem(contentSource.Span[unclesStart..content.BytesRead], uncles);
                Withdrawal[]? withdrawals = content.HasNext ? DecodeWithdrawals<OnFlag>(ref content, RlpBehaviors.None) : null;
                result = new BlockBody(transactions, uncles, withdrawals);
                reader.ExitSequence(in content, itemLength);
                reader.CheckEnd();
            }
            catch
            {
                foreach (Transaction transaction in transactions) TxDecoder.TxObjectPool.Return(transaction);
                throw;
            }

            return result;
        }

        reader.ExitSequence(in content, itemLength);
        reader.CheckEnd();
        return null;
    }

    /// <summary>Returns the pooled transactions of <paramref name="body"/> to <see cref="TxDecoder.TxObjectPool"/>.</summary>
    /// <remarks>
    /// For a body from <see cref="DecodeWithEncodedTransactions"/> or a pooled decode. Do not use its transactions after.
    /// </remarks>
    public static void ReturnTransactions(BlockBody? body)
    {
        if (body is null) return;
        foreach (Transaction transaction in body.Transactions) TxDecoder.TxObjectPool.Return(transaction);
    }

    /// <summary>Checks that the uncles in <paramref name="body"/> are in the encoding that <see cref="IHeaderDecoder"/> writes.</summary>
    /// <remarks>
    /// The uncles hash is computed from that encoding, so a body kept as received is bound to its header only then.
    /// <see cref="DecodeWithEncodedTransactions"/> runs this check itself.
    /// </remarks>
    /// <param name="body">The encoded body with its sequence prefix.</param>
    /// <param name="uncles">The uncles decoded from <paramref name="body"/>.</param>
    /// <exception cref="RlpReaderException">The uncles are not in their canonical encoding.</exception>
    public void CheckCanonicalUncles(ReadOnlySpan<byte> body, BlockHeader[] uncles)
    {
        if (uncles.Length == 0) return;

        RlpReader reader = new(body);
        RlpReader content = reader.EnterSequence(out _);
        content.SkipItem();
        CheckCanonicalUnclesItem(content.ReadEncodedItem(), uncles);
    }

    private void CheckCanonicalUnclesItem(ReadOnlySpan<byte> encodedUncles, BlockHeader[] uncles)
    {
        if (uncles.Length == 0) return;

        int length = GetUnclesLength(uncles);
        using ArrayPoolSpan<byte> canonical = new(Rlp.LengthOfSequence(length));
        RlpWriter writer = RlpWriter.ContentWriter(canonical);
        RlpSequence sequence = writer.StartSequence(length);
        foreach (BlockHeader uncle in uncles) _headerDecoder.Encode(ref writer, uncle);
        writer.EndSequence(sequence);
        if (!encodedUncles.SequenceEqual(canonical)) throw new RlpReaderException("Uncles are not in their canonical encoding");
    }

    /// <summary>Reads the transactions of an encoded block, each kept in its encoded form.</summary>
    /// <remarks>The transactions are as <see cref="DecodeWithEncodedTransactions"/> returns them, with the same rules.</remarks>
    /// <param name="block">An encoded block: a sequence of the header and then the body items. The transactions refer to these bytes.</param>
    public static Transaction[] DecodeEncodedTransactionsOfBlock(Memory<byte> block)
    {
        RlpReader items = new RlpReader(block.Span).EnterSequence(out int itemLength);
        Memory<byte> itemsSource = block[(itemLength - items.Remaining)..];
        items.SkipItem();
        return ReadEncodedTransactions(ref items, itemsSource);
    }

    /// <summary>Reads the gas limit of each transaction of an encoded block, without decoding the other fields.</summary>
    /// <remarks>
    /// The gas limit is read where the transaction types up to <see cref="TxType.SetCode"/> keep it; a transaction of
    /// another type is decoded to read it.
    /// </remarks>
    /// <param name="block">An encoded block: a sequence of the header and then the body items.</param>
    public static ulong[] ReadTransactionGasLimits(ReadOnlySpan<byte> block)
    {
        RlpReader items = new RlpReader(block).EnterSequence(out _);
        items.SkipItem();
        RlpReader transactions = items.EnterSequence(out _);
        ulong[] gasLimits = new ulong[transactions.CountItems(TransactionsCountLimit)];
        for (int i = 0; i < gasLimits.Length; i++)
        {
            ReadOnlySpan<byte> item = transactions.ReadEncodedItem();
            RlpReader itemReader = new(item);
            if (itemReader.IsNextSequence)
            {
                // nonce, gas price, gas limit
                gasLimits[i] = ReadField(itemReader.EnterSequence(out _), 2);
                continue;
            }

            ReadOnlySpan<byte> envelope = itemReader.ReadBytes();
            int gasLimitIndex = (TxType)envelope[0] switch
            {
                // chain id, nonce, gas price, gas limit
                TxType.AccessList => 3,
                // chain id, nonce, max priority fee, max fee, gas limit
                TxType.EIP1559 or TxType.Blob or TxType.SetCode => 4,
                _ => -1
            };

            if (gasLimitIndex >= 0)
            {
                gasLimits[i] = ReadField(new RlpReader(envelope[1..]).EnterSequence(out _), gasLimitIndex);
            }
            else
            {
                Transaction? transaction = null;
                RlpReader reader = new(item);
                TxDecoder.Instance.Decode(ref reader, ref transaction, RlpBehaviors.AllowUnsigned);
                gasLimits[i] = transaction!.GasLimit;
                TxDecoder.TxObjectPool.Return(transaction);
            }
        }

        return gasLimits;

        static ulong ReadField(RlpReader fields, int index)
        {
            for (int i = 0; i < index; i++) fields.SkipItem();
            return fields.DecodeULong();
        }
    }

    /// <param name="reader">Reader whose next item is the transaction sequence.</param>
    /// <param name="source">The bytes that <paramref name="reader"/> reads, from its start.</param>
    private static Transaction[] ReadEncodedTransactions(ref RlpReader reader, Memory<byte> source)
    {
        int sequenceStart = reader.BytesRead;
        RlpReader items = reader.EnterSequence(out int itemLength);
        Memory<byte> itemsSource = source.Slice(sequenceStart + itemLength - items.Remaining, items.Remaining);
        int count = items.CountItems<OnFlag>(TransactionsCountLimit);
        Transaction[] transactions = new Transaction[count];
        int read = 0;
        try
        {
            for (int i = 0; i < count; i++)
            {
                if (items.IsNextItemEmptyList()) ThrowNullArrayElement(i);
                Transaction transaction = TxDecoder.TxObjectPool.Get();
                transactions[read++] = transaction;
                if (items.IsNextSequence)
                {
                    int start = items.BytesRead;
                    transaction.Type = TxType.Legacy;
                    transaction.SetPreHashMemoryNoLock(itemsSource.Slice(start, items.ReadEncodedItem().Length));
                }
                else
                {
                    ReadOnlySpan<byte> envelope = items.ReadBytes();
                    // EIP-2718: a typed transaction starts with a type byte in [0x01, 0x7f].
                    if (envelope.IsEmpty || envelope[0] is (byte)TxType.Legacy or > 0x7f)
                        throw new RlpReaderException($"Transaction {i} is not an EIP-2718 typed transaction");

                    transaction.Type = (TxType)envelope[0];
                    transaction.SetPreHashMemoryNoLock(itemsSource.Slice(items.BytesRead - envelope.Length, envelope.Length));
                }
            }

            reader.ExitSequence(in items, itemLength);
            return transactions;
        }
        catch
        {
            for (int i = 0; i < read; i++) TxDecoder.TxObjectPool.Return(transactions[i]);
            throw;
        }
    }

    // The sealed decoder is called directly: it saves the virtual call per item, which keeps the body decode 3-7% faster than the base class array decode.
    private static Withdrawal[] DecodeWithdrawals<TCanonicalChecks>(ref RlpReader reader, RlpBehaviors itemBehaviors) where TCanonicalChecks : struct, IFlag
    {
        RlpReader items = reader.EnterSequence<TCanonicalChecks>(out int itemLength);
        Withdrawal[] withdrawals = new Withdrawal[items.CountItems<TCanonicalChecks>(WithdrawalsCountLimit)];
        for (int i = 0; i < withdrawals.Length; i++)
        {
            if (items.IsNextItemEmptyList()) ThrowNullArrayElement(i);
            withdrawals[i] = WithdrawalDecoder.Decode<TCanonicalChecks>(ref items, itemBehaviors)!;
        }

        reader.ExitSequence(in items, itemLength);
        return withdrawals;
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowNullArrayElement(int index)
        => throw new RlpReaderException($"Unexpected null array element at index {index}");

    public override void Encode(ref RlpWriter writer, BlockBody? body, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (body is null)
        {
            writer.EncodeNullObject();
            return;
        }

        (int Txs, int Uncles, int? Withdrawals) lengths = GetBodyComponentLength(body);
        RlpSequence sequence = writer.StartSequence(GetBodyLength(lengths));
        RlpSequence txs = writer.StartSequence(lengths.Txs);
        foreach (Transaction? txn in body.Transactions)
        {
            _txDecoder.Encode(ref writer, txn);
        }

        writer.EndSequence(txs);
        RlpSequence uncles = writer.StartSequence(lengths.Uncles);
        foreach (BlockHeader? uncle in body.Uncles)
        {
            _headerDecoder.Encode(ref writer, uncle);
        }

        writer.EndSequence(uncles);
        if (body.Withdrawals is not null)
        {
            RlpSequence withdrawals = writer.StartSequence(lengths.Withdrawals.GetValueOrDefault());
            foreach (Withdrawal? withdrawal in body.Withdrawals)
            {
                _withdrawalDecoderDecoder.Encode(ref writer, withdrawal);
            }

            writer.EndSequence(withdrawals);
        }

        writer.EndSequence(sequence);
    }
}
