// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text.Json;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

[TestFixture]
public class GethLikeTxTraceSizeTests
{
    [TestCaseSource(nameof(Entries))]
    public void Estimate_does_not_underestimate_compact_serialization(GethTxTraceEntry entry)
    {
        AssertSize(entry, null);
        AssertSize(entry, new Dictionary<UInt256, UInt256>());
        AssertSize(entry, new Dictionary<UInt256, UInt256> { [UInt256.MaxValue] = UInt256.Zero });
    }

    private static void AssertSize(GethTxTraceEntry entry, IDictionary<UInt256, UInt256>? storage) =>
        Assert.That(GethLikeTxTraceConverter.EstimateEntrySize(entry, storage?.Count), Is.GreaterThanOrEqualTo(SerializedSize(entry, storage)));

    private static long SerializedSize(GethTxTraceEntry entry, IDictionary<UInt256, UInt256>? storage)
    {
        ArrayBufferWriter<byte> buffer = new();
        using Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { SkipValidation = true });
        GethLikeTxTraceConverter.WriteEntry(writer, entry, storage);
        return writer.BytesCommitted + writer.BytesPending;
    }

    private static IEnumerable<GethTxTraceEntry> Entries()
    {
        foreach (long value in new[] { long.MinValue, -100, -10, -1, 0, 1, 9, 10, 99, 100, long.MaxValue })
            yield return new() { ProgramCounter = value, Refund = value, Depth = value < 0 ? int.MinValue : int.MaxValue };
        ulong[] gasValues = [0, 9, 10, 99, 100, ulong.MaxValue];
        foreach (ulong value in gasValues)
            yield return new() { Gas = value, GasCost = value };
        for (int opcode = 0; opcode <= byte.MaxValue; opcode++)
            yield return new() { Opcode = OpcodeJsonNames.GetName((Instruction)opcode) };

        string?[] strings = [null, "", "STOP", "opcode 0xc not defined", "0x0123456789abcdef", "\"\\\n\r\t\0", "<>&'+", "é漢", "😀", "\ud800", "\udc00"];
        foreach (string? text in strings)
            yield return new() { Opcode = text, Error = text };
        string?[] returnDataValues = [null, "", "0x", "0x0123456789abcdef", "0x" + new string('f', 2048)];
        foreach (string? returnData in returnDataValues)
            yield return new() { ReturnData = returnData };
        yield return new() { Opcode = new string('漢', 1024), Error = new string('\n', 1024), ReturnData = returnDataValues[^1] };
        yield return new() { Stack = Array.Empty<byte>(), Memory = Array.Empty<byte>(), Storage = new Dictionary<UInt256, UInt256>() };
        yield return new() { Stack = new byte[32] };
        byte[] maximum = new byte[32];
        Array.Fill(maximum, byte.MaxValue);
        yield return new() { Stack = maximum };
        for (int bit = 0; bit < 256; bit += 4)
        {
            byte[] word = new byte[32];
            word[31 - bit / 8] = (byte)(1 << (bit % 8));
            yield return new() { Stack = word };
        }

        Random random = new(13670);
        for (int i = 0; i < 64; i++)
        {
            byte[] stack = new byte[random.Next(0, 33) * 32];
            byte[] memory = new byte[(i % 8 == 0 ? 2048 : random.Next(0, 9)) * 32];
            random.NextBytes(stack);
            if (i % 2 == 0) random.NextBytes(memory);
            Dictionary<UInt256, UInt256> storage = [];
            for (int slot = 0; slot < i % 9; slot++)
                storage[(UInt256)(uint)slot] = slot % 2 == 0 ? UInt256.Zero : UInt256.MaxValue;
            yield return new()
            {
                ProgramCounter = random.NextInt64(),
                Opcode = strings[i % strings.Length],
                Gas = (ulong)random.NextInt64(),
                GasCost = ulong.MaxValue - (ulong)random.NextInt64(),
                Depth = random.Next(),
                Refund = i % 2 == 0 ? random.NextInt64() : null,
                Error = strings[(i + 1) % strings.Length],
                ReturnData = returnDataValues[(i + 2) % returnDataValues.Length],
                Stack = i % 3 == 0 ? (ReadOnlyMemory<byte>?)null : stack,
                Memory = i % 3 == 1 ? (ReadOnlyMemory<byte>?)null : memory,
                Storage = i % 3 == 2 ? null : storage
            };
        }
    }

    [Test]
    public void Storage_budget_counts_updates_without_deduplicating_slots([Values(-1, 0)] int boundaryOffset)
    {
        (Address Address, UInt256 Key, UInt256 Value)?[] deltas =
        [
            (TestItem.AddressA, UInt256.Zero, UInt256.Zero),
            (new Address(TestItem.AddressA.Bytes), UInt256.Zero, UInt256.MaxValue),
            (TestItem.AddressB, UInt256.Zero, UInt256.One),
            null,
            (TestItem.AddressA, UInt256.One, UInt256.MaxValue),
            (TestItem.AddressB, UInt256.One, UInt256.Zero),
            (TestItem.AddressA, UInt256.Zero, UInt256.Zero)
        ];
        int updates = 0;
        List<GethTxMemoryTraceEntry> entries = [];
        long size = 0;
        using ExecutionEnvironment environment = ExecutionEnvironment.Rent(
            null!, Address.Zero, Address.Zero, null, callDepth: 0, value: UInt256.Zero, inputData: default);
        foreach ((Address Address, UInt256 Key, UInt256 Value)? delta in deltas)
        {
            GethTxMemoryTraceEntry entry = new() { Opcode = "SSTORE", StorageDelta = delta };
            entries.Add(entry);
            size += GethLikeTxTraceConverter.EstimateEntrySize(entry, delta.HasValue ? ++updates : null);
            using ProbeTracer tracer = new(size + boundaryOffset);
            foreach (GethTxMemoryTraceEntry captured in entries) tracer.Append(captured);
            tracer.StartOperation(0, Instruction.STOP, 0, in environment);
            Assert.That(tracer.BuildResult().Entries, Has.Count.EqualTo(entries.Count + (boundaryOffset == 0 ? 1 : 0)));
        }
    }

    private sealed class ProbeTracer(long limit) : GethLikeTxMemoryTracer(null, GethTraceOptions.Default with { Limit = limit })
    {
        public void Append(GethTxMemoryTraceEntry entry) => AddTraceEntry(entry);
    }
}
