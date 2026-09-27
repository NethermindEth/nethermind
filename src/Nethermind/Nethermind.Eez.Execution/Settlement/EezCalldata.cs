// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.Core.Crypto;

namespace Nethermind.Eez.Execution.Settlement;

/// <summary>
/// Calldata of the EEZ entry points a settled window carries, and the hashes the proof commits to. Decoding accepts
/// only the canonical encoding, so the bytes that were decoded are exactly the bytes a signature covers.
/// </summary>
public static class EezCalldata
{
    public const uint PostAndVerifyBatchSelector = 0xcafef125;
    public const uint ExecuteIncomingCrossChainCallSelector = 0x8d8461d9;
    public const uint LoadExecutionTableSelector = 0xb301bc80;

    private const int Word = AbiWord.Size;
    private const int SelectorSize = sizeof(uint);
    private const int IncomingHead = 7 * Word;
    private const int TableHead = 2 * Word;

    public static PostBatch DecodePostAndVerifyBatch(ReadOnlySpan<byte> calldata)
    {
        AbiReader reader = new(Parameters(calldata, PostAndVerifyBatchSelector));
        reader.ExpectOffset(0, 0, Word);
        PostBatch batch = EezAbi.ReadPostBatch(reader, Word, out int size);
        EnsureConsumed(reader, Word + size);
        EnsureCanonical(calldata, EncodePostAndVerifyBatch(batch));
        return batch;
    }

    public static byte[] EncodePostAndVerifyBatch(PostBatch batch)
    {
        byte[] calldata = new byte[SelectorSize + Word + EezAbi.Size(batch)];
        BinaryPrimitives.WriteUInt32BigEndian(calldata, PostAndVerifyBatchSelector);
        AbiWriter writer = new(calldata.AsSpan(SelectorSize));
        writer.WriteOffset(Word);
        EezAbi.Write(ref writer, batch);
        return calldata;
    }

    public static IncomingCrossChainCall DecodeExecuteIncomingCrossChainCall(ReadOnlySpan<byte> calldata)
    {
        AbiReader reader = new(Parameters(calldata, ExecuteIncomingCrossChainCallSelector));
        int tail = IncomingHead;
        reader.ExpectOffset(2 * Word, 0, tail);
        byte[] data = reader.ReadBytes(tail, out int partSize);
        tail += partSize;
        reader.ExpectOffset(5 * Word, 0, tail);
        L2ExecutionEntry[] entries = EezAbi.ReadDynamicArray(reader, tail, EezAbi.ReadL2ExecutionEntry, out partSize);
        tail += partSize;
        reader.ExpectOffset(6 * Word, 0, tail);
        L2StaticExecutionEntry[] staticEntries = EezAbi.ReadDynamicArray(reader, tail, EezAbi.ReadL2StaticExecutionEntry, out partSize);
        EnsureConsumed(reader, tail + partSize);
        IncomingCrossChainCall call = new(reader.ReadAddress(0), reader.ReadUInt256(Word), data, reader.ReadAddress(3 * Word),
            reader.ReadUInt64(4 * Word), entries, staticEntries);
        EnsureCanonical(calldata, EncodeExecuteIncomingCrossChainCall(call));
        return call;
    }

    public static byte[] EncodeExecuteIncomingCrossChainCall(IncomingCrossChainCall call)
    {
        int data = IncomingHead;
        int entries = data + EezAbi.Size(call.Data);
        int staticEntries = entries + EezAbi.DynamicArraySize(call.Entries, EezAbi.Size);
        int length = staticEntries + EezAbi.DynamicArraySize(call.StaticEntries, EezAbi.Size);
        byte[] calldata = new byte[SelectorSize + length];
        BinaryPrimitives.WriteUInt32BigEndian(calldata, ExecuteIncomingCrossChainCallSelector);
        AbiWriter writer = new(calldata.AsSpan(SelectorSize));
        writer.Write(call.Destination);
        writer.Write(call.Value);
        writer.WriteOffset(data);
        writer.Write(call.SourceAddress);
        writer.Write(call.SourceRollup);
        writer.WriteOffset(entries);
        writer.WriteOffset(staticEntries);
        writer.WriteBytes(call.Data);
        EezAbi.WriteDynamicArray(ref writer, call.Entries, EezAbi.Size, EezAbi.Write);
        EezAbi.WriteDynamicArray(ref writer, call.StaticEntries, EezAbi.Size, EezAbi.Write);
        return calldata;
    }

    public static ExecutionTable DecodeLoadExecutionTable(ReadOnlySpan<byte> calldata)
    {
        AbiReader reader = new(Parameters(calldata, LoadExecutionTableSelector));
        int tail = TableHead;
        reader.ExpectOffset(0, 0, tail);
        L2ExecutionEntry[] entries = EezAbi.ReadDynamicArray(reader, tail, EezAbi.ReadL2ExecutionEntry, out int partSize);
        tail += partSize;
        reader.ExpectOffset(Word, 0, tail);
        L2StaticExecutionEntry[] staticEntries = EezAbi.ReadDynamicArray(reader, tail, EezAbi.ReadL2StaticExecutionEntry, out partSize);
        EnsureConsumed(reader, tail + partSize);
        ExecutionTable table = new(entries, staticEntries);
        EnsureCanonical(calldata, EncodeLoadExecutionTable(table));
        return table;
    }

    public static byte[] EncodeLoadExecutionTable(ExecutionTable table)
    {
        int entries = TableHead;
        int staticEntries = entries + EezAbi.DynamicArraySize(table.Entries, EezAbi.Size);
        int length = staticEntries + EezAbi.DynamicArraySize(table.StaticEntries, EezAbi.Size);
        byte[] calldata = new byte[SelectorSize + length];
        BinaryPrimitives.WriteUInt32BigEndian(calldata, LoadExecutionTableSelector);
        AbiWriter writer = new(calldata.AsSpan(SelectorSize));
        writer.WriteOffset(entries);
        writer.WriteOffset(staticEntries);
        EezAbi.WriteDynamicArray(ref writer, table.Entries, EezAbi.Size, EezAbi.Write);
        EezAbi.WriteDynamicArray(ref writer, table.StaticEntries, EezAbi.Size, EezAbi.Write);
        return calldata;
    }

    /// <summary><c>keccak256(abi.encode(entry))</c>, the commitment the public inputs hash binds per entry.</summary>
    public static ValueHash256 EntryHash(ExecutionEntry entry) => ValueKeccak.Compute(EncodeEntry(entry));

    /// <summary><c>abi.encode(entry)</c>.</summary>
    public static byte[] EncodeEntry(ExecutionEntry entry)
    {
        byte[] encoded = new byte[Word + EezAbi.Size(entry)];
        AbiWriter writer = new(encoded);
        writer.WriteOffset(Word);
        EezAbi.Write(ref writer, entry);
        return encoded;
    }

    /// <summary><c>keccak256(abi.encode(entry))</c> for a static entry.</summary>
    public static ValueHash256 StaticEntryHash(StaticExecutionEntry entry)
    {
        byte[] encoded = new byte[Word + EezAbi.Size(entry)];
        AbiWriter writer = new(encoded);
        writer.WriteOffset(Word);
        EezAbi.Write(ref writer, entry);
        return ValueKeccak.Compute(encoded);
    }

    private static ReadOnlySpan<byte> Parameters(ReadOnlySpan<byte> calldata, uint selector)
    {
        if (calldata.Length < SelectorSize || BinaryPrimitives.ReadUInt32BigEndian(calldata) != selector)
        {
            throw new EezAbiException($"Calldata does not call selector 0x{selector:x8}.");
        }

        return calldata[SelectorSize..];
    }

    private static void EnsureConsumed(AbiReader reader, int consumed)
    {
        if (consumed != reader.Length)
        {
            throw new EezAbiException($"Calldata has {reader.Length - consumed} bytes after its canonical encoding.");
        }
    }

    // The decoder already rejects every non-canonical form it knows; re-encoding guards against one it does not.
    private static void EnsureCanonical(ReadOnlySpan<byte> calldata, ReadOnlySpan<byte> reencoded)
    {
        if (!calldata.SequenceEqual(reencoded))
        {
            throw new EezAbiException("Calldata is not the canonical encoding of what it decodes to.");
        }
    }
}
