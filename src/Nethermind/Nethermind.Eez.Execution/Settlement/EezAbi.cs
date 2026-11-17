// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Eez.Execution.Settlement;

/// <summary>
/// Canonical ABI encoding of the EEZ settlement types. Every tuple is sized first, so encoding writes each byte once,
/// and decoding rejects any input the encoder would not have produced.
/// </summary>
internal static class EezAbi
{
    private const int Word = AbiWord.Size;

    private const int CrossChainCallHead = 8 * Word;
    private const int ExpectedCallHead = 5 * Word;
    private const int ExecutionEntryHead = 8 * Word;
    private const int StaticExecutionEntryHead = 7 * Word;
    private const int RollupProofSystemsHead = 2 * Word;
    private const int PostBatchHead = 12 * Word;
    private const int L2ExecutionEntryHead = 6 * Word;
    private const int L2StaticExecutionEntryHead = 6 * Word;
    private const int RollupUpdateSize = 4 * Word;
    private const int ExpectedRootSize = 2 * Word;

    public delegate T TupleReader<T>(AbiReader reader, int start, out int size);

    public delegate void TupleWriter<T>(ref AbiWriter writer, T value);

    // ── sizes ─────────────────────────────────────────────────────

    public static int Size(byte[] data) => Word + AbiWord.PaddedLength(data.Length);

    public static int Size(CrossChainCall call) => CrossChainCallHead + Size(call.Data);

    public static int Size(ExpectedCall call) => ExpectedCallHead + DynamicArraySize(call.Calls, Size) + Size(call.ReturnData);

    public static int Size(ExecutionEntry entry) =>
        ExecutionEntryHead
        + StaticArraySize(entry.RollupUpdates.Length, RollupUpdateSize)
        + DynamicArraySize(entry.Calls, Size)
        + DynamicArraySize(entry.ExpectedCalls, Size)
        + Size(entry.ReturnData);

    public static int Size(StaticExecutionEntry entry) =>
        StaticExecutionEntryHead
        + StaticArraySize(entry.ExpectedRoots.Length, ExpectedRootSize)
        + DynamicArraySize(entry.Calls, Size)
        + Size(entry.ReturnData);

    public static int Size(RollupProofSystems rollup) => RollupProofSystemsHead + StaticArraySize(rollup.ProofSystemIndexes.Length, Word);

    public static int Size(PostBatch batch) =>
        PostBatchHead
        + StaticArraySize(batch.ExpectedRoots.Length, ExpectedRootSize)
        + DynamicArraySize(batch.Entries, Size)
        + DynamicArraySize(batch.StaticEntries, Size)
        + StaticArraySize(batch.ProofSystems.Length, Word)
        + DynamicArraySize(batch.RollupIdsWithProofSystems, Size)
        + StaticArraySize(batch.BlobIndices.Length, Word)
        + Size(batch.CallData)
        + DynamicArraySize(batch.Proofs, Size);

    public static int Size(L2ExecutionEntry entry) =>
        L2ExecutionEntryHead + DynamicArraySize(entry.IncomingCalls, Size) + DynamicArraySize(entry.ExpectedOutgoingCalls, Size) + Size(entry.ReturnData);

    public static int Size(L2StaticExecutionEntry entry) =>
        L2StaticExecutionEntryHead + DynamicArraySize(entry.IncomingCalls, Size) + Size(entry.ReturnData);

    public static int DynamicArraySize<T>(T[] items, Func<T, int> size)
    {
        int total = Word + items.Length * Word;
        foreach (T item in items)
        {
            total += size(item);
        }

        return total;
    }

    private static int StaticArraySize(int count, int elementSize) => Word + count * elementSize;

    // ── writers ───────────────────────────────────────────────────

    public static void Write(ref AbiWriter writer, CrossChainCall call)
    {
        writer.Write(call.RevertNextNCalls);
        writer.Write(call.IsStatic);
        writer.Write(call.Gas);
        writer.Write(call.SourceAddress);
        writer.Write(call.SourceRollupId);
        writer.Write(call.TargetAddress);
        writer.Write(call.Value);
        writer.WriteOffset(CrossChainCallHead);
        writer.WriteBytes(call.Data);
    }

    public static void Write(ref AbiWriter writer, ExpectedCall call)
    {
        int calls = ExpectedCallHead;
        int returnData = calls + DynamicArraySize(call.Calls, Size);
        writer.Write(call.ExpectedHash);
        writer.WriteOffset(calls);
        writer.Write(call.RevertedOrStaticRollingHash);
        writer.Write(call.Success);
        writer.WriteOffset(returnData);
        WriteDynamicArray(ref writer, call.Calls, Size, Write);
        writer.WriteBytes(call.ReturnData);
    }

    public static void Write(ref AbiWriter writer, ExecutionEntry entry)
    {
        int rollupUpdates = ExecutionEntryHead;
        int calls = rollupUpdates + StaticArraySize(entry.RollupUpdates.Length, RollupUpdateSize);
        int expectedCalls = calls + DynamicArraySize(entry.Calls, Size);
        int returnData = expectedCalls + DynamicArraySize(entry.ExpectedCalls, Size);
        writer.WriteOffset(rollupUpdates);
        writer.Write(entry.ProxyEntryHash);
        writer.WriteOffset(calls);
        writer.WriteOffset(expectedCalls);
        writer.Write(entry.RollingHash);
        writer.Write(entry.DestinationRollupId);
        writer.Write(entry.Success);
        writer.WriteOffset(returnData);
        writer.Write((ulong)entry.RollupUpdates.Length);
        foreach (RollupUpdate update in entry.RollupUpdates)
        {
            writer.Write(update.RollupId);
            writer.Write(update.EtherDelta);
            writer.Write(update.CurrentRoot);
            writer.Write(update.NewRoot);
        }

        WriteDynamicArray(ref writer, entry.Calls, Size, Write);
        WriteDynamicArray(ref writer, entry.ExpectedCalls, Size, Write);
        writer.WriteBytes(entry.ReturnData);
    }

    public static void Write(ref AbiWriter writer, StaticExecutionEntry entry)
    {
        int roots = StaticExecutionEntryHead;
        int calls = roots + StaticArraySize(entry.ExpectedRoots.Length, ExpectedRootSize);
        int returnData = calls + DynamicArraySize(entry.Calls, Size);
        writer.WriteOffset(roots);
        writer.Write(entry.ProxyEntryHash);
        writer.WriteOffset(calls);
        writer.Write(entry.RollingHash);
        writer.Write(entry.DestinationRollupId);
        writer.Write(entry.Success);
        writer.WriteOffset(returnData);
        WriteExpectedRoots(ref writer, entry.ExpectedRoots);
        WriteDynamicArray(ref writer, entry.Calls, Size, Write);
        writer.WriteBytes(entry.ReturnData);
    }

    public static void Write(ref AbiWriter writer, RollupProofSystems rollup)
    {
        writer.Write(rollup.RollupId);
        writer.WriteOffset(RollupProofSystemsHead);
        writer.Write((ulong)rollup.ProofSystemIndexes.Length);
        foreach (ulong index in rollup.ProofSystemIndexes)
        {
            writer.Write(index);
        }
    }

    public static void Write(ref AbiWriter writer, PostBatch batch)
    {
        int roots = PostBatchHead;
        int entries = roots + StaticArraySize(batch.ExpectedRoots.Length, ExpectedRootSize);
        int staticEntries = entries + DynamicArraySize(batch.Entries, Size);
        int proofSystems = staticEntries + DynamicArraySize(batch.StaticEntries, Size);
        int rollups = proofSystems + StaticArraySize(batch.ProofSystems.Length, Word);
        int blobIndices = rollups + DynamicArraySize(batch.RollupIdsWithProofSystems, Size);
        int callData = blobIndices + StaticArraySize(batch.BlobIndices.Length, Word);
        int proofs = callData + Size(batch.CallData);
        writer.WriteOffset(roots);
        writer.WriteOffset(entries);
        writer.WriteOffset(staticEntries);
        writer.Write(batch.ImmediateEntryCount);
        writer.Write(batch.ImmediateStaticEntryCount);
        writer.WriteOffset(proofSystems);
        writer.WriteOffset(rollups);
        writer.WriteOffset(blobIndices);
        writer.WriteOffset(callData);
        writer.WriteOffset(proofs);
        writer.Write(batch.BlockNumber);
        writer.Write(batch.BindMsgSenderInPublicInput);
        WriteExpectedRoots(ref writer, batch.ExpectedRoots);
        WriteDynamicArray(ref writer, batch.Entries, Size, Write);
        WriteDynamicArray(ref writer, batch.StaticEntries, Size, Write);
        writer.Write((ulong)batch.ProofSystems.Length);
        foreach (Address proofSystem in batch.ProofSystems)
        {
            writer.Write(proofSystem);
        }

        WriteDynamicArray(ref writer, batch.RollupIdsWithProofSystems, Size, Write);
        writer.Write((ulong)batch.BlobIndices.Length);
        foreach (UInt256 blobIndex in batch.BlobIndices)
        {
            writer.Write(blobIndex);
        }

        writer.WriteBytes(batch.CallData);
        WriteDynamicArray(ref writer, batch.Proofs, Size, static (ref AbiWriter w, byte[] proof) => w.WriteBytes(proof));
    }

    public static void Write(ref AbiWriter writer, L2ExecutionEntry entry)
    {
        int incoming = L2ExecutionEntryHead;
        int outgoing = incoming + DynamicArraySize(entry.IncomingCalls, Size);
        int returnData = outgoing + DynamicArraySize(entry.ExpectedOutgoingCalls, Size);
        writer.Write(entry.ProxyEntryHash);
        writer.WriteOffset(incoming);
        writer.WriteOffset(outgoing);
        writer.Write(entry.RollingHash);
        writer.Write(entry.Success);
        writer.WriteOffset(returnData);
        WriteDynamicArray(ref writer, entry.IncomingCalls, Size, Write);
        WriteDynamicArray(ref writer, entry.ExpectedOutgoingCalls, Size, Write);
        writer.WriteBytes(entry.ReturnData);
    }

    public static void Write(ref AbiWriter writer, L2StaticExecutionEntry entry)
    {
        int incoming = L2StaticExecutionEntryHead;
        int returnData = incoming + DynamicArraySize(entry.IncomingCalls, Size);
        writer.Write(entry.ExpectedEntryIndex);
        writer.Write(entry.ProxyEntryHash);
        writer.WriteOffset(incoming);
        writer.Write(entry.RollingHash);
        writer.Write(entry.Success);
        writer.WriteOffset(returnData);
        WriteDynamicArray(ref writer, entry.IncomingCalls, Size, Write);
        writer.WriteBytes(entry.ReturnData);
    }

    public static void WriteDynamicArray<T>(ref AbiWriter writer, T[] items, Func<T, int> size, TupleWriter<T> write)
    {
        writer.Write((ulong)items.Length);
        int offset = items.Length * Word;
        foreach (T item in items)
        {
            writer.WriteOffset(offset);
            offset += size(item);
        }

        foreach (T item in items)
        {
            write(ref writer, item);
        }
    }

    private static void WriteExpectedRoots(ref AbiWriter writer, ExpectedRoot[] roots)
    {
        writer.Write((ulong)roots.Length);
        foreach (ExpectedRoot root in roots)
        {
            writer.Write(root.RollupId);
            writer.Write(root.Root);
        }
    }

    // ── readers ───────────────────────────────────────────────────

    public static CrossChainCall ReadCrossChainCall(AbiReader reader, int start, out int size)
    {
        int tail = start + CrossChainCallHead;
        reader.ExpectOffset(start + 7 * Word, start, tail);
        byte[] data = reader.ReadBytes(tail, out int dataSize);
        size = CrossChainCallHead + dataSize;
        return new CrossChainCall(
            reader.ReadUInt16(start),
            reader.ReadBool(start + Word),
            reader.ReadUInt64(start + 2 * Word),
            reader.ReadAddress(start + 3 * Word),
            reader.ReadUInt64(start + 4 * Word),
            reader.ReadAddress(start + 5 * Word),
            reader.ReadUInt256(start + 6 * Word),
            data);
    }

    public static ExpectedCall ReadExpectedCall(AbiReader reader, int start, out int size)
    {
        int tail = start + ExpectedCallHead;
        reader.ExpectOffset(start + Word, start, tail);
        CrossChainCall[] calls = ReadDynamicArray(reader, tail, ReadCrossChainCall, out int callsSize);
        tail += callsSize;
        reader.ExpectOffset(start + 4 * Word, start, tail);
        byte[] returnData = reader.ReadBytes(tail, out int returnDataSize);
        size = tail + returnDataSize - start;
        return new ExpectedCall(reader.ReadHash(start), calls, reader.ReadHash(start + 2 * Word), reader.ReadBool(start + 3 * Word), returnData);
    }

    public static ExecutionEntry ReadExecutionEntry(AbiReader reader, int start, out int size)
    {
        int tail = start + ExecutionEntryHead;
        reader.ExpectOffset(start, start, tail);
        RollupUpdate[] rollupUpdates = ReadRollupUpdates(reader, tail, out int updatesSize);
        tail += updatesSize;
        reader.ExpectOffset(start + 2 * Word, start, tail);
        CrossChainCall[] calls = ReadDynamicArray(reader, tail, ReadCrossChainCall, out int callsSize);
        tail += callsSize;
        reader.ExpectOffset(start + 3 * Word, start, tail);
        ExpectedCall[] expectedCalls = ReadDynamicArray(reader, tail, ReadExpectedCall, out int expectedSize);
        tail += expectedSize;
        reader.ExpectOffset(start + 7 * Word, start, tail);
        byte[] returnData = reader.ReadBytes(tail, out int returnDataSize);
        size = tail + returnDataSize - start;
        return new ExecutionEntry(rollupUpdates, reader.ReadHash(start + Word), calls, expectedCalls, reader.ReadHash(start + 4 * Word),
            reader.ReadUInt64(start + 5 * Word), reader.ReadBool(start + 6 * Word), returnData);
    }

    public static StaticExecutionEntry ReadStaticExecutionEntry(AbiReader reader, int start, out int size)
    {
        int tail = start + StaticExecutionEntryHead;
        reader.ExpectOffset(start, start, tail);
        ExpectedRoot[] roots = ReadExpectedRoots(reader, tail, out int rootsSize);
        tail += rootsSize;
        reader.ExpectOffset(start + 2 * Word, start, tail);
        CrossChainCall[] calls = ReadDynamicArray(reader, tail, ReadCrossChainCall, out int callsSize);
        tail += callsSize;
        reader.ExpectOffset(start + 6 * Word, start, tail);
        byte[] returnData = reader.ReadBytes(tail, out int returnDataSize);
        size = tail + returnDataSize - start;
        return new StaticExecutionEntry(roots, reader.ReadHash(start + Word), calls, reader.ReadHash(start + 3 * Word),
            reader.ReadUInt64(start + 4 * Word), reader.ReadBool(start + 5 * Word), returnData);
    }

    public static RollupProofSystems ReadRollupProofSystems(AbiReader reader, int start, out int size)
    {
        int tail = start + RollupProofSystemsHead;
        reader.ExpectOffset(start + Word, start, tail);
        int count = reader.ReadCount(tail, Word);
        ulong[] indexes = new ulong[count];
        for (int i = 0; i < count; i++)
        {
            indexes[i] = reader.ReadUInt64(tail + Word + i * Word);
        }

        size = RollupProofSystemsHead + StaticArraySize(count, Word);
        return new RollupProofSystems(reader.ReadUInt64(start), indexes);
    }

    public static PostBatch ReadPostBatch(AbiReader reader, int start, out int size)
    {
        int tail = start + PostBatchHead;
        reader.ExpectOffset(start, start, tail);
        ExpectedRoot[] roots = ReadExpectedRoots(reader, tail, out int partSize);
        tail += partSize;
        reader.ExpectOffset(start + Word, start, tail);
        ExecutionEntry[] entries = ReadDynamicArray(reader, tail, ReadExecutionEntry, out partSize);
        tail += partSize;
        reader.ExpectOffset(start + 2 * Word, start, tail);
        StaticExecutionEntry[] staticEntries = ReadDynamicArray(reader, tail, ReadStaticExecutionEntry, out partSize);
        tail += partSize;
        reader.ExpectOffset(start + 5 * Word, start, tail);
        Address[] proofSystems = ReadAddresses(reader, tail, out partSize);
        tail += partSize;
        reader.ExpectOffset(start + 6 * Word, start, tail);
        RollupProofSystems[] rollups = ReadDynamicArray(reader, tail, ReadRollupProofSystems, out partSize);
        tail += partSize;
        reader.ExpectOffset(start + 7 * Word, start, tail);
        UInt256[] blobIndices = ReadUInt256s(reader, tail, out partSize);
        tail += partSize;
        reader.ExpectOffset(start + 8 * Word, start, tail);
        byte[] callData = reader.ReadBytes(tail, out partSize);
        tail += partSize;
        reader.ExpectOffset(start + 9 * Word, start, tail);
        byte[][] proofs = ReadDynamicArray(reader, tail, static (AbiReader r, int s, out int n) => r.ReadBytes(s, out n), out partSize);
        tail += partSize;
        size = tail - start;
        return new PostBatch(roots, entries, staticEntries, reader.ReadUInt256(start + 3 * Word), reader.ReadUInt256(start + 4 * Word),
            proofSystems, rollups, blobIndices, callData, proofs, reader.ReadUInt64(start + 10 * Word), reader.ReadBool(start + 11 * Word));
    }

    public static L2ExecutionEntry ReadL2ExecutionEntry(AbiReader reader, int start, out int size)
    {
        int tail = start + L2ExecutionEntryHead;
        reader.ExpectOffset(start + Word, start, tail);
        CrossChainCall[] incoming = ReadDynamicArray(reader, tail, ReadCrossChainCall, out int incomingSize);
        tail += incomingSize;
        reader.ExpectOffset(start + 2 * Word, start, tail);
        ExpectedCall[] outgoing = ReadDynamicArray(reader, tail, ReadExpectedCall, out int outgoingSize);
        tail += outgoingSize;
        reader.ExpectOffset(start + 5 * Word, start, tail);
        byte[] returnData = reader.ReadBytes(tail, out int returnDataSize);
        size = tail + returnDataSize - start;
        return new L2ExecutionEntry(reader.ReadHash(start), incoming, outgoing, reader.ReadHash(start + 3 * Word), reader.ReadBool(start + 4 * Word), returnData);
    }

    public static L2StaticExecutionEntry ReadL2StaticExecutionEntry(AbiReader reader, int start, out int size)
    {
        int tail = start + L2StaticExecutionEntryHead;
        reader.ExpectOffset(start + 2 * Word, start, tail);
        CrossChainCall[] incoming = ReadDynamicArray(reader, tail, ReadCrossChainCall, out int incomingSize);
        tail += incomingSize;
        reader.ExpectOffset(start + 5 * Word, start, tail);
        byte[] returnData = reader.ReadBytes(tail, out int returnDataSize);
        size = tail + returnDataSize - start;
        return new L2StaticExecutionEntry(reader.ReadUInt256(start), reader.ReadHash(start + Word), incoming, reader.ReadHash(start + 3 * Word),
            reader.ReadBool(start + 4 * Word), returnData);
    }

    public static T[] ReadDynamicArray<T>(AbiReader reader, int position, TupleReader<T> read, out int size)
    {
        int count = reader.ReadCount(position, Word);
        int start = position + Word;
        int tail = start + count * Word;
        T[] items = new T[count];
        for (int i = 0; i < count; i++)
        {
            reader.ExpectOffset(start + i * Word, start, tail);
            items[i] = read(reader, tail, out int itemSize);
            tail += itemSize;
        }

        size = tail - position;
        return items;
    }

    private static RollupUpdate[] ReadRollupUpdates(AbiReader reader, int position, out int size)
    {
        int count = reader.ReadCount(position, RollupUpdateSize);
        RollupUpdate[] updates = new RollupUpdate[count];
        for (int i = 0; i < count; i++)
        {
            int at = position + Word + i * RollupUpdateSize;
            updates[i] = new RollupUpdate(reader.ReadUInt64(at), reader.ReadHash(at + 2 * Word), reader.ReadHash(at + 3 * Word), reader.ReadInt192(at + Word));
        }

        size = StaticArraySize(count, RollupUpdateSize);
        return updates;
    }

    private static ExpectedRoot[] ReadExpectedRoots(AbiReader reader, int position, out int size)
    {
        int count = reader.ReadCount(position, ExpectedRootSize);
        ExpectedRoot[] roots = new ExpectedRoot[count];
        for (int i = 0; i < count; i++)
        {
            int at = position + Word + i * ExpectedRootSize;
            roots[i] = new ExpectedRoot(reader.ReadUInt64(at), reader.ReadHash(at + Word));
        }

        size = StaticArraySize(count, ExpectedRootSize);
        return roots;
    }

    private static Address[] ReadAddresses(AbiReader reader, int position, out int size)
    {
        int count = reader.ReadCount(position, Word);
        Address[] addresses = new Address[count];
        for (int i = 0; i < count; i++)
        {
            addresses[i] = reader.ReadAddress(position + Word + i * Word);
        }

        size = StaticArraySize(count, Word);
        return addresses;
    }

    private static UInt256[] ReadUInt256s(AbiReader reader, int position, out int size)
    {
        int count = reader.ReadCount(position, Word);
        UInt256[] values = new UInt256[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = reader.ReadUInt256(position + Word + i * Word);
        }

        size = StaticArraySize(count, Word);
        return values;
    }
}
