// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.ConsensusRequests;

namespace Nethermind.Serialization.Rlp
{
    public class BlockDecoder : IRlpValueDecoder<Block>, IRlpStreamDecoder<Block>
    {
        private readonly HeaderDecoder _headerDecoder = new();
        private readonly TxDecoder _txDecoder = new();
        private readonly WithdrawalDecoder _withdrawalDecoder = new();
        private readonly ConsensusRequestDecoder _consensusRequestsDecoder = new();

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
                transactions.Add(Rlp.Decode<Transaction>(rlpStream, rlpBehaviors));
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

            List<Withdrawal>? withdrawals = DecodeWithdrawals(rlpStream, blockCheck);
            List<ConsensusRequest>? requests = DecodeRequests(rlpStream, blockCheck);

            if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
            {
                rlpStream.Check(blockCheck);
            }

            return new(header, transactions, uncleHeaders, withdrawals, requests);
        }


        private List<Withdrawal>? DecodeWithdrawals(RlpStream rlpStream, int blockCheck)
        {
            List<Withdrawal>? withdrawals = null;
            if (rlpStream.Position != blockCheck)
            {
                bool lengthWasRead = true;
                try
                {
                    rlpStream.PeekNextRlpLength();
                }
                catch
                {
                    lengthWasRead = false;
                }

                if (lengthWasRead)
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
            }

            return withdrawals;
        }

        private List<ConsensusRequest>? DecodeRequests(RlpStream rlpStream, int blockCheck)
        {
            List<ConsensusRequest>? requests = null;
            if (rlpStream.Position != blockCheck)
            {
                bool lengthWasRead = true;
                try
                {
                    rlpStream.PeekNextRlpLength();
                }
                catch
                {
                    lengthWasRead = false;
                }

                if (lengthWasRead)
                {
                    int requestsLength = rlpStream.ReadSequenceLength();
                    int requestsCheck = rlpStream.Position + requestsLength;
                    requests = new();

                    while (rlpStream.Position < requestsCheck)
                    {
                        requests.Add(Rlp.Decode<ConsensusRequest>(rlpStream));
                    }

                    rlpStream.Check(requestsCheck);
                }
            }

            return requests;
        }


        private (int Total, int Txs, int Uncles, int? Withdrawals, int? Requests) GetContentLength(Block item, RlpBehaviors rlpBehaviors)
        {
            int contentLength = _headerDecoder.GetLength(item.Header, rlpBehaviors);

            int txLength = GetTxLength(item, rlpBehaviors);
            contentLength += Rlp.LengthOfSequence(txLength);

            int unclesLength = GetUnclesLength(item, rlpBehaviors);
            contentLength += Rlp.LengthOfSequence(unclesLength);

            int? withdrawalsLength = null;
            if (item.Withdrawals is not null)
            {
                withdrawalsLength = GetWithdrawalsLength(item, rlpBehaviors);

                if (withdrawalsLength.HasValue)
                    contentLength += Rlp.LengthOfSequence(withdrawalsLength.Value);
            }

            int? consensusRequestsLength = null;
            if (item.Requests is not null)
            {
                consensusRequestsLength = GetConsensusRequestsLength(item, rlpBehaviors);

                if (consensusRequestsLength.HasValue)
                    contentLength += Rlp.LengthOfSequence(consensusRequestsLength.Value);
            }

            return (contentLength, txLength, unclesLength, withdrawalsLength, consensusRequestsLength);
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

        private int? GetConsensusRequestsLength(Block item, RlpBehaviors rlpBehaviors)
        {
            if (item.Requests is null)
                return null;

            var consensusRequestsLength = 0;

            for (int i = 0, count = item.Requests.Length; i < count; i++)
            {
                consensusRequestsLength += _consensusRequestsDecoder.GetLength(item.Requests[i], rlpBehaviors);
            }

            return consensusRequestsLength;
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
                transactions.Add(Rlp.Decode<Transaction>(ref decoderContext, rlpBehaviors));
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

            List<Withdrawal>? withdrawals = DecodeWithdrawals(ref decoderContext, blockCheck);
            List<ConsensusRequest>? requests = DecodeRequests(ref decoderContext, blockCheck);

            if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
            {
                decoderContext.Check(blockCheck);
            }

            return new(header, transactions, uncleHeaders, withdrawals, requests);
        }

        private List<Withdrawal>? DecodeWithdrawals(ref Rlp.ValueDecoderContext decoderContext, int blockCheck)
        {
            List<Withdrawal>? withdrawals = null;

            if (decoderContext.Position != blockCheck)
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

            return withdrawals;
        }

        private List<ConsensusRequest>? DecodeRequests(ref Rlp.ValueDecoderContext decoderContext, int blockCheck)
        {
            List<ConsensusRequest>? requests = null;

            if (decoderContext.Position != blockCheck)
            {
                int requestsLength = decoderContext.ReadSequenceLength();
                int requestsCheck = decoderContext.Position + requestsLength;
                requests = new();

                while (decoderContext.Position < requestsCheck)
                {
                    requests.Add(Rlp.Decode<ConsensusRequest>(ref decoderContext));
                }

                decoderContext.Check(requestsCheck);
            }

            return requests;
        }

        public Rlp Encode(Block? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                return Rlp.OfEmptySequence;
            }

            RlpStream rlpStream = new(GetLength(item, rlpBehaviors));
            Encode(rlpStream, item, rlpBehaviors);
            return new(rlpStream.Data.ToArray());
        }

        public void Encode(RlpStream stream, Block? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                stream.EncodeNullObject();
                return;
            }

            (int contentLength, int txsLength, int unclesLength, int? withdrawalsLength, int? requestsLength) = GetContentLength(item, rlpBehaviors);
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

            if (requestsLength.HasValue)
            {
                stream.StartSequence(requestsLength.Value);

                for (int i = 0; i < item.Requests.Length; i++)
                {
                    stream.Encode(item.Requests[i]);
                }
            }
        }

        public static ReceiptRecoveryBlock? DecodeToReceiptRecoveryBlock(MemoryManager<byte>? memoryManager, Memory<byte> memory, RlpBehaviors rlpBehaviors)
        {
            Rlp.ValueDecoderContext decoderContext = new Rlp.ValueDecoderContext(memory, true);

            if (decoderContext.IsNextItemNull())
            {
                decoderContext.ReadByte();
                return null;
            }

            int sequenceLength = decoderContext.ReadSequenceLength();
            int blockCheck = decoderContext.Position + sequenceLength;

            BlockHeader header = Rlp.Decode<BlockHeader>(ref decoderContext);

            int contentLength = decoderContext.ReadSequenceLength();
            int transactionCount = decoderContext.PeekNumberOfItemsRemaining(decoderContext.Position + contentLength);

            Memory<byte> transactionMemory = decoderContext.ReadMemory(contentLength);

            decoderContext.SkipItem(); // Skip uncles

            if (decoderContext.Position != blockCheck)
            {
                decoderContext.SkipItem(); // Skip withdrawals
            }
            if (decoderContext.Position != blockCheck)
            {
                decoderContext.SkipItem(); // Skip requests
            }
            if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
            {
                decoderContext.Check(blockCheck);
            }

            return new ReceiptRecoveryBlock(memoryManager, header, transactionMemory, transactionCount);
        }
    }
}
