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
        private readonly DepositDecoder _depositDecoder = new();
        private readonly ValidatorExitsDecoder _validatorExitsDecoder = new();

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
            List<Deposit>? deposits = DecodeDeposits(rlpStream, blockCheck);
            List<ValidatorExit>? validatorExits = DecodeValidatorExits(rlpStream, blockCheck);

            if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
            {
                rlpStream.Check(blockCheck);
            }

            return new(header, transactions, uncleHeaders, withdrawals, deposits, validatorExits);
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

        private List<Deposit>? DecodeDeposits(RlpStream rlpStream, int blockCheck)
        {
            List<Deposit>? deposits = null;
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
                    int depositsLength = rlpStream.ReadSequenceLength();
                    int depositsCheck = rlpStream.Position + depositsLength;
                    deposits = new();

                    while (rlpStream.Position < depositsCheck)
                    {
                        deposits.Add(Rlp.Decode<Deposit>(rlpStream));
                    }

                    rlpStream.Check(depositsCheck);
                }
            }

            return deposits;
        }

        private List<ValidatorExit>? DecodeValidatorExits(RlpStream rlpStream, int blockCheck)
        {
            List<ValidatorExit>? validatorExits = null;
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
                    int validatorExistsLength = rlpStream.ReadSequenceLength();
                    int validatorExistsCheck = rlpStream.Position + validatorExistsLength;
                    validatorExits = new();

                    while (rlpStream.Position < validatorExistsCheck)
                    {
                        validatorExits.Add(_validatorExitsDecoder.Decode(rlpStream));
                    }

                    rlpStream.Check(validatorExistsCheck);
                }
            }

            return validatorExits;
        }


        private (int Total, int Txs, int Uncles, int? Withdrawals, int? Deposits, int? ValidatorExits) GetContentLength(Block item, RlpBehaviors rlpBehaviors)
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

            int? depositsLength = null;
            if (item.Deposits is not null)
            {
                depositsLength = GetDepositsLength(item, rlpBehaviors);

                if (depositsLength.HasValue)
                    contentLength += Rlp.LengthOfSequence(depositsLength.Value);
            }

            int? validatorExitsLength = null;
            if (item.ValidatorExits is not null)
            {
                validatorExitsLength = GetValidatorExitsLength(item, rlpBehaviors);

                if (validatorExitsLength is not null)
                    contentLength += Rlp.LengthOfSequence(validatorExitsLength.Value);
            }

            return (contentLength, txLength, unclesLength, withdrawalsLength, depositsLength, validatorExitsLength);
        }

        private int? GetValidatorExitsLength(Block item, RlpBehaviors rlpBehaviors)
        {
            if (item.ValidatorExits is null)
                return null;

            int validatorExistsLength = 0;

            for (int i = 0, count = item.ValidatorExits.Length; i < count; i++)
            {
                validatorExistsLength += _validatorExitsDecoder.GetLength(item.ValidatorExits[i], rlpBehaviors);
            }

            return validatorExistsLength;
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

        private int? GetDepositsLength(Block item, RlpBehaviors rlpBehaviors)
        {
            if (item.Deposits is null)
                return null;

            var depositsLength = 0;

            for (int i = 0, count = item.Deposits.Length; i < count; i++)
            {
                depositsLength += _depositDecoder.GetLength(item.Deposits[i], rlpBehaviors);
            }

            return depositsLength;
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
            List<Deposit>? deposits = DecodeDeposits(ref decoderContext, blockCheck);
            List<ValidatorExit>? validatorExits = DecodeValidatorExits(ref decoderContext, blockCheck);

            if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
            {
                decoderContext.Check(blockCheck);
            }

            return new(header, transactions, uncleHeaders, withdrawals, deposits, validatorExits);
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

        private List<Deposit>? DecodeDeposits(ref Rlp.ValueDecoderContext decoderContext, int blockCheck)
        {
            List<Deposit>? deposits = null;

            if (decoderContext.Position != blockCheck)
            {
                int depositsLength = decoderContext.ReadSequenceLength();
                int depositsCheck = decoderContext.Position + depositsLength;
                deposits = new();

                while (decoderContext.Position < depositsCheck)
                {
                    deposits.Add(Rlp.Decode<Deposit>(ref decoderContext));
                }

                decoderContext.Check(depositsCheck);
            }

            return deposits;
        }
        private List<ValidatorExit>? DecodeValidatorExits(ref Rlp.ValueDecoderContext decoderContext, int blockCheck)
        {
            List<ValidatorExit>? validatorExits = null;

            if (decoderContext.Position != blockCheck)
            {
                int validatorExitLength = decoderContext.ReadSequenceLength();
                int validatorExitsCheck = decoderContext.Position + validatorExitLength;
                validatorExits = new();

                while (decoderContext.Position < validatorExitsCheck)
                {
                    validatorExits.Add(_validatorExitsDecoder.Decode(ref decoderContext));
                }

                decoderContext.Check(validatorExitsCheck);
            }

            return validatorExits;
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

            (int contentLength, int txsLength, int unclesLength, int? withdrawalsLength, int? depositsLength, int? validatorExitsLength) = GetContentLength(item, rlpBehaviors);
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

            if (depositsLength.HasValue)
            {
                stream.StartSequence(depositsLength.Value);

                for (int i = 0; i < item.Deposits.Length; i++)
                {
                    stream.Encode(item.Deposits[i]);
                }
            }

            if (validatorExitsLength.HasValue)
            {
                stream.StartSequence(validatorExitsLength.Value);

                for (int i = 0; i < item.ValidatorExits!.Length; i++)
                {
                    _validatorExitsDecoder.Encode(stream, item.ValidatorExits[i]);
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
            if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
            {
                decoderContext.Check(blockCheck);
            }

            return new ReceiptRecoveryBlock(memoryManager, header, transactionMemory, transactionCount);
        }
    }
}
