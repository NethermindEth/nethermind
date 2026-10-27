// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
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

    public override void PopulateLength(BlockBody? item, ref LengthStack lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            lengths.Push(Rlp.OfEmptyList.Length);
            return;
        }

        int contentLength = item.Withdrawals is { } withdrawals ? _withdrawalDecoderDecoder.PopulateSequenceAndPop(withdrawals, ref lengths) : 0;
        contentLength += _headerDecoder.PopulateSequenceAndPop(item.Uncles, ref lengths);
        contentLength += _txDecoder.PopulateSequenceAndPop(item.Transactions, ref lengths);
        lengths.Push(contentLength);
        lengths.Push(Rlp.LengthOfSequence(contentLength));
    }

    protected override BlockBody? DecodeInternal(ref RlpReader ctx, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int sequenceLength = ctx.ReadSequenceLength();
        int startingPosition = ctx.Position;
        if (sequenceLength == 0)
        {
            return null;
        }

        return DecodeUnwrapped(ref ctx, startingPosition + sequenceLength,
            usePooledTransactions: (rlpBehaviors & RlpBehaviors.SkipPooledTransactions) == 0);
    }

    /// <summary>Decodes body contents after the outer sequence prefix, with optional transaction pooling.</summary>
    /// <param name="ctx">Reader positioned at the transaction sequence.</param>
    /// <param name="lastPosition">Expected reader position after the body contents.</param>
    /// <param name="usePooledTransactions">
    /// Rent exclusively owned transactions. Return them to <see cref="TxDecoder.TxObjectPool"/> at most once,
    /// after all consumers finish. Pass false for retained blocks.
    /// </param>
    /// <remarks>The JSON-RPC <c>HashesOnlyBlockReader</c> reads the same layout without this decoder and leaves any block
    /// it does not recognise to it; a body field added here needs adding there to keep that fast path.</remarks>
    public BlockBody DecodeUnwrapped(ref RlpReader ctx, int lastPosition, bool usePooledTransactions)
    {
        Transaction[] transactions = DecodeTransactions(ref ctx, usePooledTransactions);
        try
        {
            BlockHeader[] uncles = ctx.DecodeNonNullArray(_headerDecoder, limit: UnclesCountLimit);
            Withdrawal[]? withdrawals = null;

            if (ctx.PeekNumberOfItemsRemaining(lastPosition, 1) > 0)
            {
                withdrawals = ctx.DecodeNonNullArray(_withdrawalDecoderDecoder, limit: WithdrawalsCountLimit);
            }

            ctx.Check(lastPosition);
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

    private Transaction[] DecodeTransactions(ref RlpReader ctx, bool usePooledTransactions)
    {
        int end = ctx.ReadSequenceLength() + ctx.Position;
        int count = ctx.PeekNumberOfItemsRemaining(end, TransactionsCountLimit.Limit + 1);
        ctx.GuardLimit(count, TransactionsCountLimit);
        Transaction[] transactions = new Transaction[count];
        int decoded = 0;
        try
        {
            for (int i = 0; i < count; i++)
            {
                if (ctx.PeekByte() == Rlp.EmptyListByte) RlpHelpers.ThrowNullArrayElement(i);
                Transaction? transaction = usePooledTransactions ? TxDecoder.TxObjectPool.Get() : new Transaction();
                transactions[decoded++] = transaction;
                _txDecoder.Decode(ref ctx, ref transaction);
                if (transaction is null) RlpHelpers.ThrowNullArrayElement(i);
                if (usePooledTransactions && !ReferenceEquals(transaction, transactions[i]))
                    TxDecoder.TxObjectPool.Return(transactions[i]);
                transactions[i] = transaction;
            }
            ctx.Check(end);
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

    public override void Encode<TWriter>(ref TWriter writer, BlockBody? body, ref LengthStack lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (body is null)
        {
            writer.EncodeNullObject();
            return;
        }

        writer.StartSequence(lengths.Pop());
        _txDecoder.EncodeSequence(ref writer, body.Transactions, ref lengths);
        _headerDecoder.EncodeSequence(ref writer, body.Uncles, ref lengths);
        if (body.Withdrawals is { } withdrawals)
        {
            _withdrawalDecoderDecoder.EncodeSequence(ref writer, withdrawals, ref lengths);
        }
    }
}
