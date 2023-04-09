// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;

#pragma warning disable 618

namespace Nethermind.Serialization.Rlp
{
    public class CompactReceiptStorageDecoder : IRlpStreamDecoder<TxReceipt>, IRlpValueDecoder<TxReceipt>, IRlpObjectDecoder<TxReceipt>
    {
        public static readonly CompactReceiptStorageDecoder Instance = new();

        public CompactReceiptStorageDecoder()
        {
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

            int lastCheck = rlpStream.ReadSequenceLength() + rlpStream.Position;
            List<LogEntry> logEntries = new();

            while (rlpStream.Position < lastCheck)
            {
                logEntries.Add(SlimLogEntryDecoder.Instance.Decode(rlpStream, RlpBehaviors.AllowExtraBytes));
            }

            txReceipt.Logs = logEntries.ToArray();

            bool allowExtraBytes = (rlpBehaviors & RlpBehaviors.AllowExtraBytes) != 0;
            if (!allowExtraBytes)
            {
                rlpStream.Check(lastCheck);
            }

            ReadOnlySpan<byte> bloomEntries = rlpStream.DecodeByteArraySpan();
            Bloom bloom = new Bloom();
            byte[] bloomBytes = bloom.Bytes;
            for (int i = 0; i < bloomEntries.Length; i+=2)
            {
                bloomBytes[bloomEntries[i]] = bloomEntries[i + 1];
            }
            txReceipt.Bloom = bloom;

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

            int lastCheck = decoderContext.ReadSequenceLength() + decoderContext.Position;
            List<LogEntry> logEntries = new();

            while (decoderContext.Position < lastCheck)
            {
                logEntries.Add(SlimLogEntryDecoder.Instance.Decode(ref decoderContext, RlpBehaviors.AllowExtraBytes));
            }

            txReceipt.Logs = logEntries.ToArray();

            bool allowExtraBytes = (rlpBehaviors & RlpBehaviors.AllowExtraBytes) != 0;
            if (!allowExtraBytes)
            {
                decoderContext.Check(lastCheck);
            }

            ReadOnlySpan<byte> bloomEntries = decoderContext.DecodeByteArraySpan();
            Bloom bloom = new Bloom();
            byte[] bloomBytes = bloom.Bytes;
            for (int i = 0; i < bloomEntries.Length; i+=2)
            {
                bloomBytes[bloomEntries[i]] = bloomEntries[i + 1];
            }
            txReceipt.Bloom = bloom;

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

            // Need to regenerate bloom. Can't lazily load log.
            int lastCheck = decoderContext.ReadSequenceLength() + decoderContext.Position;
            List<LogEntry> logEntries = new();
            while (decoderContext.Position < lastCheck)
            {
                logEntries.Add(SlimLogEntryDecoder.Instance.Decode(ref decoderContext, RlpBehaviors.AllowExtraBytes));
            }
            item.Logs = logEntries.ToArray();

            ReadOnlySpan<byte> bloomEntries = decoderContext.DecodeByteArraySpan();
            Bloom bloom = new Bloom();
            byte[] bloomBytes = bloom.Bytes;
            for (int i = 0; i < bloomEntries.Length; i+=2)
            {
                bloomBytes[bloomEntries[i]] = bloomEntries[i + 1];
            }
            item.Bloom = bloom.ToStructRef();
        }

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
                SlimLogEntryDecoder.Instance.Encode(rlpStream, logs[i]);
            }

            if (item.Bloom != null)
            {
                byte[] buffer = ArrayPool<byte>.Shared.Rent(256);
                int bufferLength = 0;
                byte[] bytes = item.Bloom.Bytes;
                for (int i = 0; i < bytes.Length; i++)
                {
                    if (bytes[i] != 0)
                    {
                        buffer[bufferLength] = (byte)i;
                        buffer[bufferLength + 1] = bytes[i];
                        bufferLength += 2;
                    }
                }
                rlpStream.Encode(buffer.AsSpan(0, bufferLength));
                ArrayPool<byte>.Shared.Return(buffer);
            }
            else
            {
                rlpStream.WriteByte(Rlp.EmptyArrayByte);
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

            if (item.Bloom != null)
            {
                byte[] bytes = item.Bloom.Bytes;
                byte? firstByte = null;
                int byteArraySize = 0;
                for (int i = 0; i < bytes.Length; i++)
                {
                    if (bytes[i] != 0)
                    {
                        if (firstByte == null) firstByte = (byte)i;
                        byteArraySize += 2;
                    }
                }
                contentLength += Rlp.LengthOfByteString(byteArraySize, firstByte ?? 0);
            }
            else
            {
                contentLength += 1;
            }

            return (contentLength, logsLength);
        }

        private int GetLogsLength(TxReceipt item)
        {
            int logsLength = 0;
            LogEntry[] logs = item.Logs ?? Array.Empty<LogEntry>();
            for (int i = 0; i < logs.Length; i++)
            {
                logsLength += SlimLogEntryDecoder.Instance.GetLength(logs[i]);
            }

            return logsLength;
        }

        public int GetLength(TxReceipt item, RlpBehaviors rlpBehaviors)
        {
            (int Total, int Logs) length = GetContentLength(item, rlpBehaviors);
            return Rlp.LengthOfSequence(length.Total);
        }
    }
}
