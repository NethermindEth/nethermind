// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;

namespace Nethermind.Serialization.Rlp
{
    public class BlockDecoder : IRlpValueDecoder<Block>, IRlpStreamDecoder<Block>
    {
        private readonly HeaderDecoder _headerDecoder = new();
        private readonly TxDecoder _txDecoder = new();
        private readonly WithdrawalDecoder _withdrawalDecoder = new();

        public Block? Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (rlpStream.Length == 0)
            {
                throw new RlpException($"Received a 0 length stream when decoding a {nameof(Block)}");
            }

            if (rlpStream.IsNextItemNull())
            {
                rlpStream.ReadByte();
                return null;
            }

            int sequenceLength = rlpStream.ReadSequenceLength();
            int blockCheck = rlpStream.Position + sequenceLength;

            BlockHeader header = Rlp.Decode<BlockHeader>(rlpStream);

            int transactionsSequenceLength = rlpStream.ReadSequenceLength();
            int transactionsCheck = rlpStream.Position + transactionsSequenceLength;
            List<Transaction> transactions = new();
            while (rlpStream.Position < transactionsCheck)
            {
                transactions.Add(Rlp.Decode<Transaction>(rlpStream));
            }

            rlpStream.Check(transactionsCheck);

            int unclesSequenceLength = rlpStream.ReadSequenceLength();
            int unclesCheck = rlpStream.Position + unclesSequenceLength;
            List<BlockHeader> uncleHeaders = new();
            while (rlpStream.Position < unclesCheck)
            {
                uncleHeaders.Add(Rlp.Decode<BlockHeader>(rlpStream, rlpBehaviors));
            }

            rlpStream.Check(unclesCheck);

            List<Withdrawal> withdrawals = null;

            if (header.Timestamp >= HeaderDecoder.WithdrawalTimestamp)
            {
                int withdrawalsLength = rlpStream.ReadSequenceLength();
                int withdrawalsCheck = rlpStream.Position + withdrawalsLength;
                withdrawals = new();

                while (rlpStream.Position < withdrawalsCheck)
                {
                    withdrawals.Add(Rlp.Decode<Withdrawal>(rlpStream));
                }

                rlpStream.Check(withdrawalsCheck);
            }

            if ((rlpBehaviors & RlpBehaviors.AllowExtraData) != RlpBehaviors.AllowExtraData)
            {
                rlpStream.Check(blockCheck);
            }

            return new(header, transactions, uncleHeaders, withdrawals);
        }

        private (int Total, int Txs, int Uncles, int? Withdrawals) GetContentLength(Block item, RlpBehaviors rlpBehaviors)
        {
            int contentLength = _headerDecoder.GetLength(item.Header, rlpBehaviors);

            int txLength = GetTxLength(item, rlpBehaviors);
            contentLength += Rlp.LengthOfSequence(txLength);

            int unclesLength = GetUnclesLength(item, rlpBehaviors);
            contentLength += Rlp.LengthOfSequence(unclesLength);

            int? withdrawalsLength = null;
            if (item.Header.Timestamp >= HeaderDecoder.WithdrawalTimestamp)
            {
                withdrawalsLength = GetWithdrawalsLength(item, rlpBehaviors);

                if (withdrawalsLength.HasValue)
                    contentLength += Rlp.LengthOfSequence(withdrawalsLength.Value);
            }

            return (contentLength, txLength, unclesLength, withdrawalsLength);
        }

        private int GetUnclesLength(Block item, RlpBehaviors rlpBehaviors)
        {
            int unclesLength = 0;
            for (int i = 0; i < item.Uncles.Length; i++)
            {
                unclesLength += _headerDecoder.GetLength(item.Uncles[i], rlpBehaviors);
            }

            return unclesLength;
        }

        private int GetTxLength(Block item, RlpBehaviors rlpBehaviors)
        {
            int txLength = 0;
            for (int i = 0; i < item.Transactions.Length; i++)
            {
                txLength += _txDecoder.GetLength(item.Transactions[i], rlpBehaviors);
            }

            return txLength;
        }

        private int? GetWithdrawalsLength(Block item, RlpBehaviors rlpBehaviors)
        {
            if (item.Withdrawals is null)
                return null;

            var withdrawalLength = 0;

            for (int i = 0, count = item.Withdrawals.Length; i < count; i++)
            {
                withdrawalLength += _withdrawalDecoder.GetLength(item.Withdrawals[i], rlpBehaviors);
            }

            return withdrawalLength;
        }

        public int GetLength(Block? item, RlpBehaviors rlpBehaviors)
        {
            if (item is null)
            {
                return 1;
            }

            return Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors).Total);
        }

        public Block? Decode(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (decoderContext.IsNextItemNull())
            {
                decoderContext.ReadByte();
                return null;
            }

            int sequenceLength = decoderContext.ReadSequenceLength();
            int blockCheck = decoderContext.Position + sequenceLength;

            BlockHeader header = Rlp.Decode<BlockHeader>(ref decoderContext);

            int transactionsSequenceLength = decoderContext.ReadSequenceLength();
            int transactionsCheck = decoderContext.Position + transactionsSequenceLength;
            List<Transaction> transactions = new();
            while (decoderContext.Position < transactionsCheck)
            {
                transactions.Add(Rlp.Decode<Transaction>(ref decoderContext));
            }

            decoderContext.Check(transactionsCheck);

            int unclesSequenceLength = decoderContext.ReadSequenceLength();
            int unclesCheck = decoderContext.Position + unclesSequenceLength;
            List<BlockHeader> uncleHeaders = new();
            while (decoderContext.Position < unclesCheck)
            {
                uncleHeaders.Add(Rlp.Decode<BlockHeader>(ref decoderContext, rlpBehaviors));
            }

            decoderContext.Check(unclesCheck);

            List<Withdrawal> withdrawals = null;

            if (header.Timestamp >= HeaderDecoder.WithdrawalTimestamp)
            {
                int withdrawalsLength = decoderContext.ReadSequenceLength();
                int withdrawalsCheck = decoderContext.Position + withdrawalsLength;
                withdrawals = new();

                while (decoderContext.Position < withdrawalsCheck)
                {
                    withdrawals.Add(Rlp.Decode<Withdrawal>(ref decoderContext));
                }

                decoderContext.Check(withdrawalsCheck);
            }

            if ((rlpBehaviors & RlpBehaviors.AllowExtraData) != RlpBehaviors.AllowExtraData)
            {
                decoderContext.Check(blockCheck);
            }

            return new(header, transactions, uncleHeaders, withdrawals);
        }

        public Rlp Encode(Block? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                return Rlp.OfEmptySequence;
            }

            RlpStream rlpStream = new(GetLength(item, rlpBehaviors));
            Encode(rlpStream, item, rlpBehaviors);
            return new(rlpStream.Data);
        }

        public void Encode(RlpStream stream, Block? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                stream.EncodeNullObject();
                return;
            }

            (int contentLength, int txsLength, int unclesLength, int? withdrawalsLength) = GetContentLength(item, rlpBehaviors);
            stream.StartSequence(contentLength);
            stream.Encode(item.Header);
            stream.StartSequence(txsLength);
            for (int i = 0; i < item.Transactions.Length; i++)
            {
                stream.Encode(item.Transactions[i]);
            }

            stream.StartSequence(unclesLength);
            for (int i = 0; i < item.Uncles.Length; i++)
            {
                stream.Encode(item.Uncles[i]);
            }

            if (withdrawalsLength.HasValue)
            {
                stream.StartSequence(withdrawalsLength.Value);

                for (int i = 0; i < item.Withdrawals.Length; i++)
                {
                    stream.Encode(item.Withdrawals[i]);
                }
            }
        }
    }
}
