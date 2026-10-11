// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text.Json;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.GethStyle.Custom;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Evm.Test.Tracing;

public class GethLikeTxTraceConverterTests
{
    private readonly EthereumJsonSerializer _serializer = new();

    [Test]
    public void Write_null()
    {
        string result = _serializer.Serialize((GethLikeTxTrace?)null);

        Assert.That(result, Is.EqualTo("null"));
    }

    [TestCaseSource(nameof(TraceAndJsonSource))]
    public void Write_traces(GethLikeTxTrace trace, string json)
    {
        string result = _serializer.Serialize(trace);

        Assert.That(JsonElement.DeepEquals(
            JsonDocument.Parse(result).RootElement,
            JsonDocument.Parse(json).RootElement),
            result);
    }

    [TestCaseSource(nameof(CustomValueTracerResults))]
    public void Write_custom_tracer_result(object value, string expected)
    {
        GethLikeTxTrace trace = new()
        {
            CustomTracerResult = new GethLikeCustomTrace { Value = value }
        };

        string result = _serializer.Serialize(trace);

        Assert.That(JsonElement.DeepEquals(
            JsonDocument.Parse(result).RootElement,
            JsonDocument.Parse(expected).RootElement),
            result);
    }

    [Test]
    public void Read_null()
    {
        GethLikeTxTrace result = _serializer.Deserialize<GethLikeTxTrace>("null");

        Assert.That(result, Is.Null);
    }

    [TestCaseSource(nameof(TraceAndJsonSource))]
    public void Read_traces(GethLikeTxTrace expectedTrace, string json)
    {
        GethLikeTxTrace result = _serializer.Deserialize<GethLikeTxTrace>(json);

        AssertTraceEquivalent(result, expectedTrace);
    }


    [TestCaseSource(nameof(CustomValueTracerResults))]
    public void Read_custom_tracer_result_throws(object expectedValue, string json) => Assert.Throws<JsonException>(() => _serializer.Deserialize<GethLikeTxTrace>(json));

    private void AssertTraceEquivalent(GethLikeTxTrace actual, GethLikeTxTrace expected)
    {
        string actualJson = _serializer.Serialize(actual);
        string expectedJson = _serializer.Serialize(expected);
        using JsonDocument actualDocument = JsonDocument.Parse(actualJson);
        using JsonDocument expectedDocument = JsonDocument.Parse(expectedJson);

        Assert.That(JsonElement.DeepEquals(actualDocument.RootElement, expectedDocument.RootElement), actualJson);
    }

    private static IEnumerable<TestCaseData> TraceAndJsonSource()
    {
        yield return new TestCaseData(
            new GethLikeTxTrace { Gas = 1, ReturnValue = [0x01] },
            """{ "gas": 1, "failed": false, "returnValue": "0x01", "structLogs": [] }""")
            .SetName("Gas1_NoEntries");
        yield return new TestCaseData(
            new GethLikeTxTrace
            {
                Gas = 100,
                Failed = false,
                ReturnValue = [0x01, 0x02, 0x03],
                Entries =
                [
                    new()
                    {
                        Storage = new Dictionary<UInt256, UInt256>
                        {
                            { (UInt256)1, (UInt256)2 },
                            { (UInt256)3, (UInt256)4 },
                        },
                        Memory = (ReadOnlyMemory<byte>?)new byte[64]
                        {
                            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 5,
                            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 6
                        },
                        Stack = (ReadOnlyMemory<byte>?)new byte[64]
                        {
                            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 7,
                            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 8
                        },
                        Opcode = "STOP",
                        Gas = 22000,
                        GasCost = 1,
                        Depth = 1
                    }
                ]
            },
            """
            {
              "gas" : 100,
              "failed" : false,
              "returnValue" : "0x010203",
              "structLogs" : [ {
                "pc" : 0,
                "op" : "STOP",
                "gas" : 22000,
                "gasCost" : 1,
                "depth" : 1,
                "stack" : [ "0x7", "0x8" ],
                "memory" : [ "0x0000000000000000000000000000000000000000000000000000000000000005", "0x0000000000000000000000000000000000000000000000000000000000000006" ],
                "storage" : {
                  "0x0000000000000000000000000000000000000000000000000000000000000001" : "0x0000000000000000000000000000000000000000000000000000000000000002",
                  "0x0000000000000000000000000000000000000000000000000000000000000003" : "0x0000000000000000000000000000000000000000000000000000000000000004"
                }
              } ]
            }
            """)
            .SetName("Gas100_1Entry");
    }

    private static IEnumerable<TestCaseData> CustomValueTracerResults()
    {
        yield return new TestCaseData(1, "1").SetName("Custom_Int");
        yield return new TestCaseData("1", "\"1\"").SetName("Custom_String");
        yield return new TestCaseData(new[] { 1, 2 }, "[1, 2]").SetName("Custom_Array");
        yield return new TestCaseData(new { a = 1, b = 2 }, "{ \"a\": 1, \"b\": 2 }").SetName("Custom_Object");
    }

    [TestCaseSource(nameof(Entries))]
    public void Estimate_does_not_underestimate_compact_serialization(GethTxTraceEntry entry)
    {
        AssertSize(entry, null);
        AssertSize(entry, new Dictionary<UInt256, UInt256>());
        AssertSize(entry, new Dictionary<UInt256, UInt256> { [UInt256.MaxValue] = UInt256.Zero });
    }

    private static void AssertSize(GethTxTraceEntry entry, IDictionary<UInt256, UInt256>? storage) =>
        Assert.That(GethLikeTxTraceConverter.EstimateEntrySize(entry, storage?.Count), Is.GreaterThanOrEqualTo(SerializedSize(entry, storage)));

    [Test]
    public void Hex_return_data_increases_estimate_by_its_serialized_size([Values(1, 32, 1024)] int byteCount)
    {
        GethTxTraceEntry empty = new() { ReturnData = "0x" };
        GethTxTraceEntry populated = new() { ReturnData = "0x" + new string('f', byteCount * 2) };

        Assert.That(GethLikeTxTraceConverter.EstimateEntrySize(populated, null) - GethLikeTxTraceConverter.EstimateEntrySize(empty, null),
            Is.EqualTo(SerializedSize(populated, null) - SerializedSize(empty, null)));
    }

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
        string?[] returnData = [null, "", "0x", "0x0123456789abcdef", "0x" + new string('f', 2048)];
        foreach (string? data in returnData)
            yield return new() { ReturnData = data };
        yield return new() { Opcode = new string('漢', 1024), Error = new string('\n', 1024), ReturnData = returnData[^1] };
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
                ReturnData = returnData[i % returnData.Length],
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
