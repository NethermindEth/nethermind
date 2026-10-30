// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
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
        => item is null ? Rlp.OfEmptyList.Length : Rlp.LengthOfSequence(GetBodyLength(item));

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
        => (rlpBehaviors & RlpBehaviors.TrustedInput) != 0 ? Decode<OffFlag>(ref reader, rlpBehaviors) : Decode<OnFlag>(ref reader, rlpBehaviors);

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
        => (rlpBehaviors & RlpBehaviors.TrustedInput) != 0
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

    // The sealed decoder is called directly: a call through the array decode of the base class is several times slower per item.
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
