// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.Eip7928;

namespace Nethermind.Crypto
{
    /// <summary>
    /// RLP write sink that feeds encoded bytes directly into a Keccak hash accumulator.
    /// </summary>
    public readonly struct KeccakRlpStream
    {
        private static readonly HeaderDecoder _headerDecoder = new();
        private static readonly BlockDecoder _blockDecoder = new();
        private static readonly BlockInfoDecoder _blockInfoDecoder = new();
        private static readonly TxDecoder _txDecoder = TxDecoder.Instance;
        private static readonly WithdrawalDecoder _withdrawalDecoder = new();
        private static readonly BlockAccessListDecoder _blockAccessListDecoder = BlockAccessListDecoder.Instance;
        private static readonly LogEntryDecoder _logEntryDecoder = LogEntryDecoder.Instance;
        private static readonly ValueRlpWriteSink _writeToKeccak = WriteToKeccak;
        private static readonly ValueRlpWriteByteSink _writeByteToKeccak = WriteByteToKeccak;

        private readonly KeccakHash? _keccakHash;

        private KeccakHash Hash => _keccakHash ?? throw new InvalidOperationException("Keccak RLP stream is not initialized.");

        public Hash256 GetHash() => new(Hash.GenerateValueHash());

        public ValueHash256 GetValueHash() => Hash.GenerateValueHash();

        public KeccakRlpStream()
        {
            KeccakHash keccakHash = KeccakHash.Create();
            _keccakHash = keccakHash;
        }

        public void Write(ReadOnlySpan<byte> bytesToWrite) => Hash.Update(bytesToWrite);

        public void WriteByte(byte byteToWrite) => Hash.Update(MemoryMarshal.CreateSpan(ref byteToWrite, 1));

        public void Write(byte[] bytesToWrite) => Write(bytesToWrite.AsSpan());

        public void EncodeArray<T>(T?[]? items, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            ValueRlpWriter writer = AsValueWriter();
            Rlp.GetDecoder<T>().Encode(ref writer, items, rlpBehaviors);
        }

        public void Encode(Block value)
        {
            ValueRlpWriter writer = AsValueWriter();
            _blockDecoder.Encode(ref writer, value);
        }

        public void Encode(BlockHeader value, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            ValueRlpWriter writer = AsValueWriter();
            _headerDecoder.Encode(ref writer, value, rlpBehaviors);
        }

        public void Encode(Transaction value, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            ValueRlpWriter writer = AsValueWriter();
            _txDecoder.Encode(ref writer, value, rlpBehaviors);
        }

        public void Encode(Withdrawal value)
        {
            ValueRlpWriter writer = AsValueWriter();
            _withdrawalDecoder.Encode(ref writer, value);
        }

        public void Encode(LogEntry value)
        {
            ValueRlpWriter writer = AsValueWriter();
            _logEntryDecoder.Encode(ref writer, value);
        }

        public void Encode(BlockInfo value)
        {
            ValueRlpWriter writer = AsValueWriter();
            _blockInfoDecoder.Encode(ref writer, value);
        }

        public void Encode(ReadOnlyBlockAccessList value)
        {
            ValueRlpWriter writer = AsValueWriter();
            _blockAccessListDecoder.Encode(ref writer, value);
        }

        public void Encode(GeneratedBlockAccessList value)
        {
            ValueRlpWriter writer = AsValueWriter();
            _blockAccessListDecoder.Encode(ref writer, value);
        }

        public void StartByteArray(int contentLength, bool firstByteLessThan128)
        {
            ValueRlpWriter writer = AsValueWriter();
            writer.StartByteArray(contentLength, firstByteLessThan128);
        }

        public void StartSequence(int contentLength)
        {
            ValueRlpWriter writer = AsValueWriter();
            writer.StartSequence(contentLength);
        }

        public void WriteByteArrayList(IByteArrayList? list)
        {
            ValueRlpWriter writer = AsValueWriter();
            writer.WriteByteArrayList(list);
        }

        public void Encode(Hash256? keccak)
        {
            ValueRlpWriter writer = AsValueWriter();
            writer.Encode(keccak);
        }

        public void Encode(in ValueHash256? keccak)
        {
            ValueRlpWriter writer = AsValueWriter();
            writer.Encode(in keccak);
        }

        public void Encode(Hash256[] keccaks)
        {
            ValueRlpWriter writer = AsValueWriter();
            writer.Encode(keccaks);
        }

        public void Encode(ValueHash256[] keccaks)
        {
            ValueRlpWriter writer = AsValueWriter();
            writer.Encode(keccaks);
        }

        public void Encode(IReadOnlyList<Hash256> keccaks)
        {
            ValueRlpWriter writer = AsValueWriter();
            writer.Encode(keccaks);
        }

        public void Encode(IReadOnlyList<ValueHash256> keccaks)
        {
            ValueRlpWriter writer = AsValueWriter();
            writer.Encode(keccaks);
        }

        public void Encode(Address? address)
        {
            ValueRlpWriter writer = AsValueWriter();
            writer.Encode(address);
        }

        public void Encode(Rlp? rlp)
        {
            ValueRlpWriter writer = AsValueWriter();
            writer.Encode(rlp);
        }

        public void Encode(Bloom? bloom)
        {
            ValueRlpWriter writer = AsValueWriter();
            writer.Encode(bloom);
        }

        public void Encode(byte value)
        {
            ValueRlpWriter writer = AsValueWriter();
            writer.Encode(value);
        }

        public void Encode(bool value)
        {
            ValueRlpWriter writer = AsValueWriter();
            writer.Encode(value);
        }

        public void Encode(int value)
        {
            ValueRlpWriter writer = AsValueWriter();
            writer.Encode(value);
        }

        public void Encode(uint value)
        {
            ValueRlpWriter writer = AsValueWriter();
            writer.Encode(value);
        }

        public void Encode(long value)
        {
            ValueRlpWriter writer = AsValueWriter();
            writer.Encode(value);
        }

        public void Encode(ulong value)
        {
            ValueRlpWriter writer = AsValueWriter();
            writer.Encode(value);
        }

        public void Encode(in UInt256 value, int length = -1)
        {
            ValueRlpWriter writer = AsValueWriter();
            writer.Encode(in value, length);
        }

        public void Encode(in EvmWord value)
        {
            ValueRlpWriter writer = AsValueWriter();
            writer.Encode(in value);
        }

        public void Encode(string? value)
        {
            ValueRlpWriter writer = AsValueWriter();
            writer.Encode(value);
        }

        public void Encode(byte[] input)
        {
            ValueRlpWriter writer = AsValueWriter();
            writer.Encode(input);
        }

        public void Encode(Memory<byte>? input)
        {
            ValueRlpWriter writer = AsValueWriter();
            writer.Encode(input);
        }

        public void Encode(in ReadOnlyMemory<byte> input)
        {
            ValueRlpWriter writer = AsValueWriter();
            writer.Encode(in input);
        }

        public void Encode(ReadOnlySpan<byte> input)
        {
            ValueRlpWriter writer = AsValueWriter();
            writer.Encode(input);
        }

        public void Encode(byte[][] arrays)
        {
            ValueRlpWriter writer = AsValueWriter();
            writer.Encode(arrays);
        }

        public void EncodeNullObject()
        {
            ValueRlpWriter writer = AsValueWriter();
            writer.EncodeNullObject();
        }

        public void EncodeEmptyByteArray()
        {
            ValueRlpWriter writer = AsValueWriter();
            writer.EncodeEmptyByteArray();
        }

        public void WriteZero(int length)
        {
            Span<byte> zeros = stackalloc byte[Math.Min(length, 256)];
            zeros.Clear();
            while (length > 0)
            {
                int chunkLength = Math.Min(length, zeros.Length);
                Write(zeros[..chunkLength]);
                length -= chunkLength;
            }
        }

        public ValueRlpWriter AsValueWriter() => new(Hash, _writeToKeccak, _writeByteToKeccak);

        private static void WriteToKeccak(object sink, ReadOnlySpan<byte> bytesToWrite) =>
            ((KeccakHash)sink).Update(bytesToWrite);

        private static void WriteByteToKeccak(object sink, byte byteToWrite) =>
            ((KeccakHash)sink).Update(MemoryMarshal.CreateReadOnlySpan(ref byteToWrite, 1));
    }
}
