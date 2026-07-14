// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Serialization.Rlp;

[method: DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(BlockBodyDecoder))]
public sealed class BlockBodyDecoder(IHeaderDecoder? headerDecoder = null) : RlpDecoder<BlockBody>
{
    public static RlpLimit TransactionsCountLimit => RlpLimit.For<BlockBody>(
        checked((int)(RlpLimit.MaxBlockGas / GasCostOf.Transaction + 1)),
        nameof(BlockBody.Transactions)
    );

    private static readonly RlpLimit UnclesCountLimit = RlpLimit.For<BlockBody>(2, nameof(BlockBody.Uncles));

    // Actual consensus-level max is 16, see MAX_WITHDRAWALS_PER_PAYLOAD at https://github.com/ethereum/consensus-specs/blob/master/specs/capella/beacon-chain.md
    // Increased here for compatibility with execution spec tests and benchmarks
    public static readonly RlpLimit WithdrawalsCountLimit = RlpLimit.For<BlockBody>(64_000, nameof(BlockBody.Withdrawals));

    private readonly TxDecoder _txDecoder = TxDecoder.Instance;
    private readonly IHeaderDecoder _headerDecoder = headerDecoder ?? new HeaderDecoder();
    private readonly WithdrawalDecoder _withdrawalDecoderDecoder = new();

    private static BlockBodyDecoder? _instance;
    public static BlockBodyDecoder Instance => _instance ??= new BlockBodyDecoder();

    public override int GetLength(BlockBody item, RlpBehaviors rlpBehaviors) => Rlp.LengthOfSequence(GetBodyLength(item));

    public int GetBodyLength(BlockBody b)
    {
        (int txs, int uncles, int? withdrawals) = GetBodyComponentLength(b);
        return Rlp.LengthOfSequence(txs) +
               Rlp.LengthOfSequence(uncles) +
               (withdrawals is not null ? Rlp.LengthOfSequence(withdrawals.Value) : 0);
    }

    public (int Txs, int Uncles, int? Withdrawals) GetBodyComponentLength(BlockBody b) =>
    (
        GetTxLength(b.Transactions),
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

    protected override BlockBody? DecodeInternal(ref RlpReader ctx, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int sequenceLength = ctx.ReadSequenceLength();
        int startingPosition = ctx.Position;
        if (sequenceLength == 0)
        {
            return null;
        }

        return DecodeUnwrapped(ref ctx, startingPosition + sequenceLength);
    }

    public BlockBody? DecodeUnwrapped(ref RlpReader ctx, int lastPosition)
    {
        Transaction[] transactions = ctx.DecodeArray(_txDecoder, limit: TransactionsCountLimit);
        BlockHeader[] uncles = ctx.DecodeArray(_headerDecoder, limit: UnclesCountLimit);
        Withdrawal[]? withdrawals = null;

        if (ctx.PeekNumberOfItemsRemaining(lastPosition, 1) > 0)
        {
            withdrawals = ctx.DecodeArray(_withdrawalDecoderDecoder, limit: WithdrawalsCountLimit);
        }

        ctx.Check(lastPosition);
        return new BlockBody(transactions, uncles, withdrawals);
    }

    public override void Encode<TWriter>(ref TWriter writer, BlockBody body, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        writer.StartSequence(GetBodyLength(body));
        writer.StartSequence(GetTxLength(body.Transactions));
        foreach (Transaction? txn in body.Transactions)
        {
            _txDecoder.Encode(ref writer, txn);
        }

        writer.StartSequence(GetUnclesLength(body.Uncles));
        foreach (BlockHeader? uncle in body.Uncles)
        {
            _headerDecoder.Encode(ref writer, uncle);
        }

        if (body.Withdrawals is not null)
        {
            writer.StartSequence(GetWithdrawalsLength(body.Withdrawals));
            foreach (Withdrawal? withdrawal in body.Withdrawals)
            {
                _withdrawalDecoderDecoder.Encode(ref writer, withdrawal);
            }
        }
    }
}
