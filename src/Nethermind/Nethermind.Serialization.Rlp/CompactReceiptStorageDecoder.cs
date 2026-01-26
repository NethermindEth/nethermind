// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using static Nethermind.Serialization.Rlp.Rlp;

#pragma warning disable 618

namespace Nethermind.Serialization.Rlp
{
    [Decoder(RlpDecoderKey.Storage)]
    public sealed class CompactReceiptStorageDecoder : RlpValueDecoder<TxReceipt>, IRlpObjectDecoder<TxReceipt>, IReceiptRefDecoder
    {
        public static readonly CompactReceiptStorageDecoder Instance = new();

        public CompactReceiptStorageDecoder()
        {
        }

        protected override TxReceipt? DecodeInternal(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (rlpStream.IsNextItemNull())
            {
                rlpStream.ReadByte();
                return null;
            }

            TxReceipt txReceipt = new();
            int receiptEnd = rlpStream.ReadSequenceLength() + rlpStream.Position;

            byte[] firstItem = rlpStream.DecodeByteArray();
            if (firstItem.Length == 1)
            {
                txReceipt.StatusCode = firstItem[0];
            }
            else
            {
                txReceipt.PostTransactionState = firstItem.Length == 0 ? null : new Hash256(firstItem);
            }

            txReceipt.Sender = rlpStream.DecodeAddress();
            txReceipt.GasUsedTotal = rlpStream.DecodePositiveLong();

            int sequenceLength = rlpStream.ReadSequenceLength();
            int lastCheck = sequenceLength + rlpStream.Position;
            using ArrayPoolListRef<LogEntry> logEntries = new(sequenceLength * 2 / Rlp.LengthOfAddressRlp);

            while (rlpStream.Position < lastCheck)
            {
                logEntries.Add(CompactLogEntryDecoder.Decode(rlpStream, RlpBehaviors.AllowExtraBytes));
            }

            txReceipt.Logs = logEntries.ToArray();

            bool allowExtraBytes = (rlpBehaviors & RlpBehaviors.AllowExtraBytes) != 0;
            if (!allowExtraBytes)
            {
                rlpStream.Check(lastCheck);
            }

            // EIP-7778: Read GasSpent if present in the stream
            if (rlpStream.Position < receiptEnd)
            {
                txReceipt.GasSpent = rlpStream.DecodePositiveLong();
            }

            // Handle any remaining extra bytes
            if (rlpStream.Position < receiptEnd)
            {
                if (allowExtraBytes)
                {
                    rlpStream.Position = receiptEnd;
                }
                // Note: We don't throw here since the check was already done above
            }

            txReceipt.Bloom = new Bloom(txReceipt.Logs);

            return txReceipt;
        }

        protected override TxReceipt? DecodeInternal(ref Rlp.ValueDecoderContext decoderContext,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (decoderContext.IsNextItemNull())
            {
                decoderContext.ReadByte();
                return null;
            }

            TxReceipt txReceipt = new();
            int receiptEnd = decoderContext.ReadSequenceLength() + decoderContext.Position;

            byte[] firstItem = decoderContext.DecodeByteArray();
            if (firstItem.Length == 1)
            {
                txReceipt.StatusCode = firstItem[0];
            }
            else
            {
                txReceipt.PostTransactionState = firstItem.Length == 0 ? null : new Hash256(firstItem);
            }

            txReceipt.Sender = decoderContext.DecodeAddress();
            txReceipt.GasUsedTotal = decoderContext.DecodePositiveLong();

            int sequenceLength = decoderContext.ReadSequenceLength();
            int lastCheck = sequenceLength + decoderContext.Position;

            // Don't know the size exactly, I'll just assume its just an address and add some margin
            using ArrayPoolListRef<LogEntry> logEntries = new(sequenceLength * 2 / Rlp.LengthOfAddressRlp);
            while (decoderContext.Position < lastCheck)
            {
                logEntries.Add(CompactLogEntryDecoder.Decode(ref decoderContext, RlpBehaviors.AllowExtraBytes));
            }

            txReceipt.Logs = logEntries.ToArray();

            bool allowExtraBytes = (rlpBehaviors & RlpBehaviors.AllowExtraBytes) != 0;
            if (!allowExtraBytes)
            {
                decoderContext.Check(lastCheck);
            }

            // EIP-7778: Read GasSpent if present in the stream
            if (decoderContext.Position < receiptEnd)
            {
                txReceipt.GasSpent = decoderContext.DecodePositiveLong();
            }

            // Handle any remaining extra bytes
            if (decoderContext.Position < receiptEnd && allowExtraBytes)
            {
                decoderContext.Position = receiptEnd;
            }

            txReceipt.Bloom = new Bloom(txReceipt.Logs);

            return txReceipt;
        }

        public void DecodeStructRef(scoped ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors,
            out TxReceiptStructRef item)
        {
            // Note: This method runs at 2.5 million times/sec on my machine
            item = new TxReceiptStructRef();

            if (decoderContext.IsNextItemNull())
            {
                decoderContext.ReadByte();
                return;
            }

            (int prefixLength, int contentLength) = decoderContext.PeekPrefixAndContentLength();
            int receiptEnd = decoderContext.Position + prefixLength + contentLength;
            decoderContext.SkipLength();

            ReadOnlySpan<byte> firstItem = decoderContext.DecodeByteArraySpan(RlpLimit.L32);
            if (firstItem.Length == 1)
            {
                item.StatusCode = firstItem[0];
            }
            else
            {
                item.PostTransactionState =
                    firstItem.Length == 0 ? new Hash256StructRef() : new Hash256StructRef(firstItem);
            }

            decoderContext.DecodeAddressStructRef(out item.Sender);
            item.GasUsedTotal = decoderContext.DecodePositiveLong();

            (int PrefixLength, int ContentLength) =
                decoderContext.PeekPrefixAndContentLength();
            int logsBytes = ContentLength + PrefixLength;
            item.LogsRlp = decoderContext.Data.Slice(decoderContext.Position, logsBytes);
            decoderContext.SkipItem();

            // EIP-7778: Read GasSpent if present in the stream
            bool allowExtraBytes = (rlpBehaviors & RlpBehaviors.AllowExtraBytes) != 0;
            if (decoderContext.Position < receiptEnd)
            {
                item.GasSpent = decoderContext.DecodePositiveLong();
            }

            // Handle any remaining extra bytes
            if (decoderContext.Position < receiptEnd && allowExtraBytes)
            {
                decoderContext.Position = receiptEnd;
            }
        }

        public void DecodeLogEntryStructRef(scoped ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors none,
            out LogEntryStructRef current)
        {
            CompactLogEntryDecoder.DecodeLogEntryStructRef(ref decoderContext, none, out current);
        }

        public Hash256[] DecodeTopics(Rlp.ValueDecoderContext valueDecoderContext)
        {
            return CompactLogEntryDecoder.DecodeTopics(valueDecoderContext);
        }

        // Refstruct decode does not generate bloom
        public bool CanDecodeBloom => false;

        public Rlp Encode(TxReceipt item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            RlpStream rlpStream = new(GetLength(item, rlpBehaviors));
            Encode(rlpStream, item, rlpBehaviors);
            return new Rlp(rlpStream.Data.ToArray());
        }

        public override void Encode(RlpStream rlpStream, TxReceipt? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                rlpStream.EncodeNullObject();
                return;
            }

            (int totalContentLength, int logsLength) = GetContentLength(item, rlpBehaviors);

            bool isEip658receipts = (rlpBehaviors & RlpBehaviors.Eip658Receipts) == RlpBehaviors.Eip658Receipts;
            bool isEip7778Receipts = (rlpBehaviors & RlpBehaviors.Eip7778Receipts) == RlpBehaviors.Eip7778Receipts;

            // Note: Any byte saved here is about 3GB on mainnet.
            rlpStream.StartSequence(totalContentLength);
            if (isEip658receipts)
            {
                rlpStream.Encode(item.StatusCode);
            }
            else
            {
                rlpStream.Encode(item.PostTransactionState);
            }

            rlpStream.Encode(item.Sender);
            rlpStream.Encode(item.GasUsedTotal);

            rlpStream.StartSequence(logsLength);

            LogEntry[] logs = item.Logs ?? [];
            for (int i = 0; i < logs.Length; i++)
            {
                CompactLogEntryDecoder.Encode(rlpStream, logs[i]);
            }

            // EIP-7778: Encode GasSpent if flag is set and value is present
            if (isEip7778Receipts && item.GasSpent.HasValue)
            {
                rlpStream.Encode(item.GasSpent.Value);
            }
        }

        private static (int Total, int Logs) GetContentLength(TxReceipt? item, RlpBehaviors rlpBehaviors)
        {
            int contentLength = 0;
            if (item is null)
            {
                return (contentLength, 0);
            }

            bool isEip658Receipts = (rlpBehaviors & RlpBehaviors.Eip658Receipts) == RlpBehaviors.Eip658Receipts;
            if (isEip658Receipts)
            {
                contentLength += Rlp.LengthOf(item.StatusCode);
            }
            else
            {
                contentLength += Rlp.LengthOf(item.PostTransactionState);
            }

            contentLength += Rlp.LengthOf(item.Sender);
            contentLength += Rlp.LengthOf(item.GasUsedTotal);

            int logsLength = GetLogsLength(item);
            contentLength += Rlp.LengthOfSequence(logsLength);

            // EIP-7778: Include GasSpent in content length if flag is set and value is present
            bool isEip7778Receipts = (rlpBehaviors & RlpBehaviors.Eip7778Receipts) == RlpBehaviors.Eip7778Receipts;
            if (isEip7778Receipts && item.GasSpent.HasValue)
            {
                contentLength += Rlp.LengthOf(item.GasSpent.Value);
            }

            return (contentLength, logsLength);
        }

        private static int GetLogsLength(TxReceipt item)
        {
            int logsLength = 0;
            LogEntry[] logs = item.Logs ?? [];
            for (int i = 0; i < logs.Length; i++)
            {
                logsLength += CompactLogEntryDecoder.Instance.GetLength(logs[i]);
            }

            return logsLength;
        }

        public override int GetLength(TxReceipt item, RlpBehaviors rlpBehaviors)
        {
            (int Total, _) = GetContentLength(item, rlpBehaviors);
            return Rlp.LengthOfSequence(Total);
        }
    }
}
