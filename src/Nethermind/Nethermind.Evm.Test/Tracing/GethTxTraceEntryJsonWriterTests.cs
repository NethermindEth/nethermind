// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Evm.Test.Tracing;

[TestFixture]
public class GethTxTraceEntryJsonWriterTests
{
    [Test]
    public void Manual_writer_emits_same_bytes_as_JsonSerializer_for_full_entry()
    {
        GethTxMemoryTraceEntry entry = new()
        {
            ProgramCounter = 42,
            Opcode = "ADD",
            Gas = 1_000_000,
            GasCost = 3,
            Depth = 2,
            Error = null,
            Stack = ["0x1", "0x2"],
            Memory = ["0x0000000000000000000000000000000000000000000000000000000000000020"],
            Storage = new Dictionary<string, string>
            {
                ["0000000000000000000000000000000000000000000000000000000000000020"] = "0xff",
            },
        };

        AssertByteEqual(entry);
    }

    [Test]
    public void Manual_writer_emits_same_bytes_when_optional_fields_are_null()
    {
        GethTxMemoryTraceEntry entry = new()
        {
            ProgramCounter = 0,
            Opcode = "STOP",
            Gas = 0,
            GasCost = 0,
            Depth = 1,
            Error = null,
            Stack = null,
            Memory = null,
            Storage = null,
        };

        AssertByteEqual(entry);
    }

    [Test]
    public void Manual_writer_emits_same_bytes_when_error_is_set()
    {
        GethTxMemoryTraceEntry entry = new()
        {
            ProgramCounter = 100,
            Opcode = "REVERT",
            Gas = 1000,
            GasCost = 0,
            Depth = 3,
            Error = "out of gas",
            Stack = [],
            Memory = [],
            Storage = new Dictionary<string, string>(),
        };

        AssertByteEqual(entry);
    }

    private static void AssertByteEqual(GethTxTraceEntry entry)
    {
        JsonWriterOptions options = new() { SkipValidation = true };

        ArrayBufferWriter<byte> referenceSink = new();
        using (Utf8JsonWriter referenceWriter = new(referenceSink, options))
        {
            JsonSerializer.Serialize(referenceWriter, entry, EthereumJsonSerializer.JsonOptions);
        }

        ArrayBufferWriter<byte> manualSink = new();
        using (Utf8JsonWriter manualWriter = new(manualSink, options))
        {
            GethTxTraceEntryJsonWriter.Write(manualWriter, entry);
        }

        string referenceJson = Encoding.UTF8.GetString(referenceSink.WrittenSpan);
        string manualJson = Encoding.UTF8.GetString(manualSink.WrittenSpan);

        manualJson.Should().Be(referenceJson);
    }
}
