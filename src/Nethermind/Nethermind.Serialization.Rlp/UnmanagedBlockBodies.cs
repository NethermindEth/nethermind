// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Core;

/// <summary>
/// A BlockBody[] that must be explicitly disposed or there will be memory leak. May uses netty's buffer directly.
/// I don't like the name too. Any idea?
/// </summary>
public class UnmanagedBlockBodies: IDisposable
{
    private BlockBody?[]? _rawBodies = null;

    private IMemoryOwner<byte>? _memoryOwner = null;
    private Memory<byte> _memory;
    private int _contentLength = 0;

    public UnmanagedBlockBodies(Memory<byte> memory, IMemoryOwner<byte>? memoryOwner = null)
    {
        _memory = memory;
        _memoryOwner = memoryOwner;

        Rlp.ValueDecoderContext ctx = new Rlp.ValueDecoderContext(memory);
        (_, _contentLength) = ctx.PeekPrefixAndContentLength();
    }

    public UnmanagedBlockBodies(BlockBody?[] bodies)
    {
        _rawBodies = bodies;
    }

    public BlockBody?[] DeserializeBodies()
    {
        if (_rawBodies != null)
        {
            return _rawBodies;
        }

        List<BlockBody?> bodies = new List<BlockBody?>();
        Iterator iterator = new Iterator(_memory);
        while (iterator.TryGetNext(out LazyBlockBody blockBody))
        {
            bodies.Add(blockBody.Deserialize());
        }

        return bodies.ToArray();
    }

    public Iterator IterateBodies()
    {
        if (_rawBodies != null)
        {
            return new Iterator(_rawBodies);
        }

        return new Iterator(_memory);
    }

    public void Dispose()
    {
        _memoryOwner?.Dispose();
    }

    public ref struct Iterator
    {
        private BlockBody[]? _blockBodies;
        private int _index = 0;

        private Rlp.ValueDecoderContext _ctx;
        private int _endPosition;

        internal Iterator(Memory<byte> memory)
        {
            _ctx = new Rlp.ValueDecoderContext(memory);
            _endPosition = _ctx.ReadSequenceLength() + _ctx.Position;
        }

        internal Iterator(BlockBody[] bodies)
        {
            _blockBodies = bodies;
            _index = 0;
        }

        public bool TryGetNext(out LazyBlockBody blockBody)
        {
            if (_blockBodies != null)
            {
                if (_index >= _blockBodies.Length)
                {
                    blockBody = new LazyBlockBody(Span<byte>.Empty);
                    return false;
                }

                blockBody = new LazyBlockBody(_blockBodies[_index]);
                _index++;
                return true;
            }

            if (_ctx.Position >= _endPosition)
            {
                blockBody = new LazyBlockBody(Span<byte>.Empty);
                return false;
            }

            blockBody = new LazyBlockBody(_ctx.ReadNextItem());
            return true;
        }
    }

    public ref struct LazyBlockBody
    {
        private BlockBody? _body;
        private Span<byte> _span;
        public LazyBlockBody(Span<byte> span)
        {
            _span = span;
        }

        public LazyBlockBody(BlockBody? body)
        {
            _body = body;
        }

        public BlockBody Deserialize() {
            if (_body != null)
            {
                return _body;
            }

            return RlpEncoder.Instance.DeserializeBody(_span);
        }
    }

    public (int total, int contentLength) RlpLength()
    {
        if (_rawBodies != null)
        {
            return RlpEncoder.Instance.RlpLength(_rawBodies);
        }

        return (_memory.Length, _contentLength);
    }

    public void SerializeBodies(NettyRlpStream stream)
    {
        if (_rawBodies != null)
        {
            RlpEncoder.Instance.SerializeBodies(stream, _rawBodies);
            return;
        }

        stream.Write(_memory.Span);
    }

    public class RlpEncoder
    {
        private static RlpEncoder? _encoder = null;
        public static RlpEncoder Instance => _encoder ?? new RlpEncoder();

        // TODO: static
        private readonly TxDecoder _txDecoder = new TxDecoder();
        private readonly HeaderDecoder _headerDecoder = new HeaderDecoder();
        private readonly WithdrawalDecoder _withdrawalDecoderDecoder = new WithdrawalDecoder();

        public void SerializeBodies(RlpStream stream, BlockBody?[] bodies)
        {
            (int _, int contentLength) = RlpLength(bodies);
            stream.StartSequence(contentLength);

            foreach (BlockBody? body in bodies)
            {
                if (body == null)
                {
                    stream.Encode(Rlp.OfEmptySequence);
                }
                else
                {
                    SerializeBody(stream, body);
                }
            }
        }

        public (int total, int contentLength) Length(BlockBody?[] bodies)
        {
            int contentLength = bodies.Select(b => b == null
                ? Rlp.OfEmptySequence.Length
                : Rlp.LengthOfSequence(GetBodyLength(b))
            ).Sum();
            return (Rlp.LengthOfSequence(contentLength), contentLength);
        }

        private int GetBodyLength(BlockBody? b)
        {
            if (b.Withdrawals != null)
            {
                return Rlp.LengthOfSequence(GetTxLength(b.Transactions)) +
                       Rlp.LengthOfSequence(GetUnclesLength(b.Uncles)) + Rlp.LengthOfSequence(GetWithdrawalsLength(b.Withdrawals));
            }
            return Rlp.LengthOfSequence(GetTxLength(b.Transactions)) +
                   Rlp.LengthOfSequence(GetUnclesLength(b.Uncles));
        }

        private void SerializeBody(RlpStream stream, BlockBody? body)
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

            if (body.Withdrawals != null)
            {
                stream.StartSequence(GetWithdrawalsLength(body.Withdrawals));
                foreach (Withdrawal? withdrawal in body.Withdrawals)
                {
                    stream.Encode(withdrawal);
                }
            }
        }

        public (int total, int contentLength) RlpLength(BlockBody?[] bodies)
        {
            int contentLength = bodies.Select(b => b == null
                ? Rlp.OfEmptySequence.Length
                : Rlp.LengthOfSequence(GetBodyLength(b))
            ).Sum();
            return (Rlp.LengthOfSequence(contentLength), contentLength);
        }


        private int GetTxLength(Transaction[] transactions)
        {
            return transactions.Sum(t => _txDecoder.GetLength(t, RlpBehaviors.None));
        }

        private int GetUnclesLength(BlockHeader[] headers)
        {

            return headers.Sum(t => _headerDecoder.GetLength(t, RlpBehaviors.None));
        }

        private int GetWithdrawalsLength(Withdrawal[] withdrawals)
        {

            return withdrawals.Sum(t => _withdrawalDecoderDecoder.GetLength(t, RlpBehaviors.None));
        }

        public BlockBody DeserializeBody(Span<byte> span)
        {
            Rlp.ValueDecoderContext ctx = new(span);
            int sequenceLength = ctx.ReadSequenceLength();
            int startingPosition = ctx.Position;
            if (sequenceLength == 0)
            {
                return null;
            }

            // quite significant allocations (>0.5%) here based on a sample 3M blocks sync
            // (just on these delegates)
            Transaction[] transactions = ctx.DecodeArray<Transaction>();
            BlockHeader[] uncles = ctx.DecodeArray<BlockHeader>();
            Withdrawal[]? withdrawals = null;
            if (ctx.PeekNumberOfItemsRemaining(startingPosition + sequenceLength, 1) > 0)
            {
                withdrawals = ctx.DecodeArray<Withdrawal>();
            }

            return new BlockBody(transactions, uncles, withdrawals);
        }
    }
}
