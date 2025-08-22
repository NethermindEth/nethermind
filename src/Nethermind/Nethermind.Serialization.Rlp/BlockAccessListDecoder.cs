// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.BlockAccessLists;

namespace Nethermind.Serialization.Rlp;

public class BlockAccessListDecoder : IRlpValueDecoder<BlockAccessList>, IRlpStreamDecoder<BlockAccessList>
{
    // private readonly TxDecoder _txDecoder = TxDecoder.Instance;
    // private readonly HeaderDecoder _headerDecoder = new();
    // private readonly WithdrawalDecoder _withdrawalDecoderDecoder = new();

    private static BlockAccessListDecoder? _instance = null;
    public static BlockAccessListDecoder Instance => _instance ??= new();

    // public int GetLength(BlockBody item, RlpBehaviors rlpBehaviors)
    // {
    //     return Rlp.LengthOfSequence(GetBodyLength(item));
    // }

    // public int GetBodyLength(BlockBody b)
    // {
    //     (int txs, int uncles, int? withdrawals) = GetBodyComponentLength(b);
    //     return Rlp.LengthOfSequence(txs) +
    //            Rlp.LengthOfSequence(uncles) +
    //            (withdrawals is not null ? Rlp.LengthOfSequence(withdrawals.Value) : 0);
    // }

    // public (int Txs, int Uncles, int? Withdrawals) GetBodyComponentLength(BlockBody b) =>
    // (
    //     GetTxLength(b.Transactions),
    //     GetUnclesLength(b.Uncles),
    //     b.Withdrawals is not null ? GetWithdrawalsLength(b.Withdrawals) : null
    // );

    // private int GetTxLength(Transaction[] transactions)
    // {
    //     if (transactions.Length == 0) return 0;

    //     int sum = 0;
    //     foreach (Transaction tx in transactions)
    //     {
    //         sum += _txDecoder.GetLength(tx, RlpBehaviors.None);
    //     }

    //     return sum;
    // }

    // public BlockBody? Decode(ref Rlp.ValueDecoderContext ctx, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    // {
    //     int sequenceLength = ctx.ReadSequenceLength();
    //     int startingPosition = ctx.Position;
    //     if (sequenceLength == 0)
    //     {
    //         return null;
    //     }

    //     return DecodeUnwrapped(ref ctx, startingPosition + sequenceLength);
    // }

    // public BlockBody? DecodeUnwrapped(ref Rlp.ValueDecoderContext ctx, int lastPosition)
    // {

    //     // quite significant allocations (>0.5%) here based on a sample 3M blocks sync
    //     // (just on these delegates)
    //     Transaction[] transactions = ctx.DecodeArray(_txDecoder);
    //     BlockHeader[] uncles = ctx.DecodeArray(_headerDecoder);
    //     Withdrawal[]? withdrawals = null;

    //     if (ctx.PeekNumberOfItemsRemaining(lastPosition, 1) > 0)
    //     {
    //         withdrawals = ctx.DecodeArray(_withdrawalDecoderDecoder);
    //     }

    //     return new BlockBody(transactions, uncles, withdrawals);
    // }

    // public BlockBody Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    // {
    //     Span<byte> span = rlpStream.PeekNextItem();
    //     Rlp.ValueDecoderContext ctx = new Rlp.ValueDecoderContext(span);
    //     BlockBody response = Decode(ref ctx, rlpBehaviors);
    //     rlpStream.SkipItem();

    //     return response;
    // }

    // public void Encode(RlpStream stream, BlockBody body, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    // {
    //     stream.StartSequence(GetBodyLength(body));
    //     stream.StartSequence(GetTxLength(body.Transactions));
    //     foreach (Transaction? txn in body.Transactions)
    //     {
    //         stream.Encode(txn);
    //     }

    //     stream.StartSequence(GetUnclesLength(body.Uncles));
    //     foreach (BlockHeader? uncle in body.Uncles)
    //     {
    //         stream.Encode(uncle);
    //     }

    //     if (body.Withdrawals is not null)
    //     {
    //         stream.StartSequence(GetWithdrawalsLength(body.Withdrawals));
    //         foreach (Withdrawal? withdrawal in body.Withdrawals)
    //         {
    //             stream.Encode(withdrawal);
    //         }
    //     }
    // }

    BlockAccessList IRlpValueDecoder<BlockAccessList>.Decode(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors)
    {
        throw new NotImplementedException();
    }

    public int GetLength(BlockAccessList item, RlpBehaviors rlpBehaviors)
    {
        throw new NotImplementedException();
    }

    BlockAccessList IRlpStreamDecoder<BlockAccessList>.Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors)
    {
        throw new NotImplementedException();
    }

    public void Encode(RlpStream stream, BlockAccessList item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        throw new NotImplementedException();
    }
}
