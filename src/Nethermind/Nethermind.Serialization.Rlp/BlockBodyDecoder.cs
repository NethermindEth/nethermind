// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.ConsensusRequests;

namespace Nethermind.Serialization.Rlp;

public class BlockBodyDecoder : IRlpValueDecoder<BlockBody>
{
    private readonly TxDecoder _txDecoder = TxDecoder.Instance;
    private readonly HeaderDecoder _headerDecoder = new();
    private readonly WithdrawalDecoder _withdrawalDecoderDecoder = new();
    private readonly ConsensusRequestDecoder _requestsDecoder = ConsensusRequestDecoder.Instance;

    public static BlockBodyDecoder Instance = new BlockBodyDecoder();

    private BlockBodyDecoder()
    {
    }

    public int GetLength(BlockBody item, RlpBehaviors rlpBehaviors)
    {
        return Rlp.LengthOfSequence(GetBodyLength(item));
    }

    public int GetBodyLength(BlockBody b)
    {
        (int txs, int uncles, int? withdrawals, int? requests) = GetBodyComponentLength(b);
        return Rlp.LengthOfSequence(txs) +
               Rlp.LengthOfSequence(uncles) +
               (withdrawals is not null ? Rlp.LengthOfSequence(withdrawals.Value) : 0) +
               (requests is not null ? Rlp.LengthOfSequence(requests.Value) : 0);
    }

    public (int Txs, int Uncles, int? Withdrawals, int? Requests) GetBodyComponentLength(BlockBody b) =>
        (
            GetTxLength(b.Transactions),
            GetUnclesLength(b.Uncles),
            (b.Withdrawals is not null ? GetWithdrawalsLength(b.Withdrawals) : null),
            (b.Requests is not null ? GetRequestsLength(b.Requests) : null)
        );

    private int GetTxLength(Transaction[] transactions) => transactions.Sum(t => _txDecoder.GetLength(t, RlpBehaviors.None));

    private int GetUnclesLength(BlockHeader[] headers) => headers.Sum(t => _headerDecoder.GetLength(t, RlpBehaviors.None));

    private int GetWithdrawalsLength(Withdrawal[] withdrawals) => withdrawals.Sum(t => _withdrawalDecoderDecoder.GetLength(t, RlpBehaviors.None));

    private int GetRequestsLength(ConsensusRequest[] requests) => requests.Sum(t => _requestsDecoder.GetLength(t, RlpBehaviors.None));

    public BlockBody? Decode(ref Rlp.ValueDecoderContext ctx, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int sequenceLength = ctx.ReadSequenceLength();
        int startingPosition = ctx.Position;
        if (sequenceLength == 0)
        {
            return null;
        }

        return DecodeUnwrapped(ref ctx, startingPosition + sequenceLength);
    }

    public BlockBody? DecodeUnwrapped(ref Rlp.ValueDecoderContext ctx, int lastPosition)
    {

        // quite significant allocations (>0.5%) here based on a sample 3M blocks sync
        // (just on these delegates)
        Transaction[] transactions = ctx.DecodeArray(_txDecoder);
        BlockHeader[] uncles = ctx.DecodeArray(_headerDecoder);
        Withdrawal[]? withdrawals = null;
        ConsensusRequest[]? requests = null;
        if (ctx.PeekNumberOfItemsRemaining(lastPosition, 1) > 0)
        {
            withdrawals = ctx.DecodeArray(_withdrawalDecoderDecoder);
        }

        if (ctx.PeekNumberOfItemsRemaining(lastPosition, 1) > 0)
        {
            requests = ctx.DecodeArray(_requestsDecoder);
        }

        return new BlockBody(transactions, uncles, withdrawals, requests);
    }

    public void Serialize(RlpStream stream, BlockBody body)
    {
        stream.StartSequence(GetBodyLength(body));
        stream.StartSequence(GetTxLength(body.Transactions));
        foreach (Transaction? txn in body.Transactions)
        {
            stream.Encode(txn);
        }

        stream.StartSequence(GetUnclesLength(body.Uncles));
        foreach (BlockHeader? uncle in body.Uncles)
        {
            stream.Encode(uncle);
        }

        if (body.Withdrawals is not null)
        {
            stream.StartSequence(GetWithdrawalsLength(body.Withdrawals));
            foreach (Withdrawal? withdrawal in body.Withdrawals)
            {
                stream.Encode(withdrawal);
            }
        }

        if (body.Requests is not null)
        {
            stream.StartSequence(GetRequestsLength(body.Requests));
            foreach (ConsensusRequest? request in body.Requests)
            {
                stream.Encode(request);
            }
        }
    }
}
