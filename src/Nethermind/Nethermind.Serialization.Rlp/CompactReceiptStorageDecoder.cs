// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

#pragma warning disable 618

namespace Nethermind.Serialization.Rlp
{
    public class CompactReceiptStorageDecoder : IRlpStreamDecoder<TxReceipt>, IRlpValueDecoder<TxReceipt>, IRlpObjectDecoder<TxReceipt>, IReceiptRefDecoder
    {
        public static readonly CompactReceiptStorageDecoder Instance = new(false);
        private bool _encodeBloom;

        public CompactReceiptStorageDecoder(bool encodeBloom = false)
        {
            _encodeBloom = encodeBloom;
        }

        public TxReceipt? Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (rlpStream.IsNextItemNull())
            {
                rlpStream.ReadByte();
                return null;
            }

            TxReceipt txReceipt = new();
            rlpStream.ReadSequenceLength();

            byte[] firstItem = rlpStream.DecodeByteArray();
            if (firstItem.Length == 1)
            {
                txReceipt.StatusCode = firstItem[0];
            }
            else
            {
                txReceipt.PostTransactionState = firstItem.Length == 0 ? null : new Keccak(firstItem);
            }

            txReceipt.Sender = rlpStream.DecodeAddress();
            txReceipt.GasUsedTotal = (long)rlpStream.DecodeUBigInt();

            int sequenceLength = rlpStream.ReadSequenceLength();
            int lastCheck = sequenceLength + rlpStream.Position;
            using ArrayPoolList<LogEntry> logEntries = new(sequenceLength * 2 / Rlp.LengthOfAddressRlp);

            while (rlpStream.Position < lastCheck)
            {
                logEntries.Add(CompactLogEntryDecoder.Instance.Decode(rlpStream, RlpBehaviors.AllowExtraBytes));
            }

            txReceipt.Logs = logEntries.ToArray();

            bool allowExtraBytes = (rlpBehaviors & RlpBehaviors.AllowExtraBytes) != 0;
            if (!allowExtraBytes)
            {
                rlpStream.Check(lastCheck);
            }

            if (_encodeBloom)
            {
                sequenceLength = rlpStream.ReadSequenceLength();
                lastCheck = sequenceLength + rlpStream.Position;
                txReceipt.Bloom = new Bloom();
                Span<byte> theByte = txReceipt.Bloom.Bytes;

                int position = 0;
                while (rlpStream.Position < lastCheck)
                {
                    position += rlpStream.DecodeInt();
                    int bytePos = position / 8;
                    int byteIndex = position % 8;
                    theByte[bytePos] = (byte)(theByte[bytePos] | (1 << (7 - byteIndex)));
                }
            }
            else
            {
                txReceipt.Bloom = new Bloom(txReceipt.Logs);
            }

            return txReceipt;
        }

        public TxReceipt? Decode(ref Rlp.ValueDecoderContext decoderContext,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (decoderContext.IsNextItemNull())
            {
                decoderContext.ReadByte();
                return null;
            }

            TxReceipt txReceipt = new();
            decoderContext.ReadSequenceLength();

            byte[] firstItem = decoderContext.DecodeByteArray();
            if (firstItem.Length == 1)
            {
                txReceipt.StatusCode = firstItem[0];
            }
            else
            {
                txReceipt.PostTransactionState = firstItem.Length == 0 ? null : new Keccak(firstItem);
            }

            txReceipt.Sender = decoderContext.DecodeAddress();
            txReceipt.GasUsedTotal = (long)decoderContext.DecodeUBigInt();

            int sequenceLength = decoderContext.ReadSequenceLength();
            int lastCheck = sequenceLength + decoderContext.Position;

            // Don't know the size exactly, I'll just assume its just an address and add some margin
            using ArrayPoolList<LogEntry> logEntries = new(sequenceLength * 2 / Rlp.LengthOfAddressRlp);
            while (decoderContext.Position < lastCheck)
            {
                logEntries.Add(CompactLogEntryDecoder.Instance.Decode(ref decoderContext, RlpBehaviors.AllowExtraBytes));
            }

            txReceipt.Logs = logEntries.ToArray();

            bool allowExtraBytes = (rlpBehaviors & RlpBehaviors.AllowExtraBytes) != 0;
            if (!allowExtraBytes)
            {
                decoderContext.Check(lastCheck);
            }

            if (_encodeBloom)
            {
                sequenceLength = decoderContext.ReadSequenceLength();
                lastCheck = sequenceLength + decoderContext.Position;
                txReceipt.Bloom = new Bloom();
                Span<byte> theByte = txReceipt.Bloom.Bytes;

                int position = 0;
                while (decoderContext.Position < lastCheck)
                {
                    position += decoderContext.DecodeInt();
                    int bytePos = position / 8;
                    int byteIndex = position % 8;
                    theByte[bytePos] = (byte)(theByte[bytePos] | (1 << (7 - byteIndex)));
                }
            }
            else
            {
                txReceipt.Bloom = new Bloom(txReceipt.Logs);
            }

            return txReceipt;
        }

        public void DecodeStructRef(scoped ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors,
            out TxReceiptStructRef item)
        {
            item = new TxReceiptStructRef();

            if (decoderContext.IsNextItemNull())
            {
                decoderContext.ReadByte();
                return;
            }

            decoderContext.SkipLength();

            Span<byte> firstItem = decoderContext.DecodeByteArraySpan();
            if (firstItem.Length == 1)
            {
                item.StatusCode = firstItem[0];
            }
            else
            {
                item.PostTransactionState =
                    firstItem.Length == 0 ? new KeccakStructRef() : new KeccakStructRef(firstItem);
            }

            item.Sender = (decoderContext.DecodeAddress() ?? Address.Zero).ToStructRef();
            item.GasUsedTotal = (long)decoderContext.DecodeUBigInt();

            (int PrefixLength, int ContentLength) peekPrefixAndContentLength =
                decoderContext.PeekPrefixAndContentLength();
            int logsBytes = peekPrefixAndContentLength.ContentLength + peekPrefixAndContentLength.PrefixLength;
            item.LogsRlp = decoderContext.Data.Slice(decoderContext.Position, logsBytes);
            decoderContext.SkipItem();

            if (_encodeBloom)
            {
                int sequenceLength = decoderContext.ReadSequenceLength();
                int lastCheck = sequenceLength + decoderContext.Position;
                item.Bloom = new BloomStructRef(new byte[256]);
                Span<byte> theByte = item.Bloom.Bytes;

                int position = 0;
                while (decoderContext.Position < lastCheck)
                {
                    position += decoderContext.DecodeInt();
                    int bytePos = position / 8;
                    int byteIndex = position % 8;
                    theByte[bytePos] = (byte)(theByte[bytePos] | (1 << (7 - byteIndex)));
                }
            }
        }

        public void DecodeLogEntryStructRef(scoped ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors none,
            out LogEntryStructRef current)
        {
            CompactLogEntryDecoder.Instance.DecodeLogEntryStructRef(ref decoderContext, none, out current);
        }

        public Keccak[] DecodeTopics(Rlp.ValueDecoderContext valueDecoderContext)
        {
            return CompactLogEntryDecoder.Instance.DecodeTopics(valueDecoderContext);
        }

        public bool CanDecodeBloom => false;

        public Rlp Encode(TxReceipt item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            RlpStream rlpStream = new(GetLength(item, rlpBehaviors));
            Encode(rlpStream, item, rlpBehaviors);
            return new Rlp(rlpStream.Data);
        }

        public void Encode(RlpStream rlpStream, TxReceipt? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                rlpStream.EncodeNullObject();
                return;
            }

            (int totalContentLength, int logsLength) = GetContentLength(item, rlpBehaviors);

            bool isEip658receipts = (rlpBehaviors & RlpBehaviors.Eip658Receipts) == RlpBehaviors.Eip658Receipts;

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

            LogEntry[] logs = item.Logs ?? Array.Empty<LogEntry>();
            for (int i = 0; i < logs.Length; i++)
            {
                CompactLogEntryDecoder.Instance.Encode(rlpStream, logs[i]);
            }

            if (_encodeBloom)
            {
                EncodeBloom(rlpStream, item.Bloom);
            }
        }

        private void EncodeBloom(RlpStream rlpStream, Bloom? bloom)
        {
            if (bloom == null)
            {
                rlpStream.Write(Rlp.OfEmptySequence.Bytes);
                return;
            }

            int bloomLength = BloomLength(bloom);
            rlpStream.StartSequence(bloomLength);

            byte[] bytes = bloom.Bytes;

            int prevPointer = 0;

            for (int i = 0; i < bytes.Length; i+=4)
            {
                uint data = (uint) (bytes[i] << 24 | bytes[i + 1] << 16 | bytes[i + 2] << 8 | bytes[i + 3] << 0);
                int byteOffset = i * 8;
                while (data != 0)
                {
                    int idx = NextLowestSetBit(ref data);
                    int newPointer = byteOffset + idx;
                    rlpStream.Encode(newPointer - prevPointer);
                    prevPointer = newPointer;
                }
            }
        }

        private (int Total, int Logs) GetContentLength(TxReceipt? item, RlpBehaviors rlpBehaviors)
        {
            int contentLength = 0;
            int logsLength = 0;
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

            logsLength = GetLogsLength(item);
            contentLength += Rlp.LengthOfSequence(logsLength);

            if (_encodeBloom)
            {
                contentLength += Rlp.LengthOfSequence(BloomLength(item.Bloom));
            }

            return (contentLength, logsLength);
        }

        private int GetLogsLength(TxReceipt item)
        {
            int logsLength = 0;
            LogEntry[] logs = item.Logs ?? Array.Empty<LogEntry>();
            for (int i = 0; i < logs.Length; i++)
            {
                logsLength += CompactLogEntryDecoder.Instance.GetLength(logs[i]);
            }

            return logsLength;
        }

        public int GetLength(TxReceipt item, RlpBehaviors rlpBehaviors)
        {
            (int Total, int Logs) length = GetContentLength(item, rlpBehaviors);
            return Rlp.LengthOfSequence(length.Total);
        }

        private int BloomLength(Bloom? bloom)
        {
            if (bloom == null)
            {
                return 1;
            }

            byte[] bytes = bloom.Bytes;
            int contentLength = 0;
            int prevPointer = 0;

            for (int i = 0; i < bytes.Length; i+=4)
            {
                // Its big endien here?
                uint data = (uint) (bytes[i] << 24 | bytes[i + 1] << 16 | bytes[i + 2] << 8 | bytes[i + 3] << 0);
                int byteOffset = i * 8;
                while (data != 0)
                {
                    int idx = NextLowestSetBit(ref data);
                    int newPointer = byteOffset + idx;
                    contentLength += Rlp.LengthOf(newPointer - prevPointer);
                    prevPointer = newPointer;
                }
            }

            return contentLength;
        }

        private int NextLowestSetBit(ref uint data)
        {
            // Get the lowest set bit and its index from data
            // TODO: bit manipulation and lookup
            int index = 0;
            uint data2 = data;
            uint leftMost = (uint) 1 << 31;
            while ((data2 & leftMost) == 0)
            {
                data2 <<= 1;
                index++;
            }

            data = (uint)(data & ~(leftMost >> index));

            return index;
        }
    }
}
