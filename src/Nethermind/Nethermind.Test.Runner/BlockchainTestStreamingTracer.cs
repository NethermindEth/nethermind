// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Text.Json;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.Test.Runner;

/// <summary>
/// Streaming tracer for blockchain tests that writes all traces to stderr.
/// Compatible with go-ethereum's blocktest tracing output format.
/// Outputs consolidated traces across all blocks and transactions in a single stream.
/// </summary>
public class BlockchainTestStreamingTracer : IBlockTracer, IDisposable
{
    private readonly TextWriter _output;
    private readonly GethTraceOptions _options;
    private GethLikeTxFileTracer? _currentTxTracer;

    public BlockchainTestStreamingTracer(GethTraceOptions options, TextWriter? output = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _output = output ?? Console.Error;
    }

    public bool IsTracingRewards => false;

    public void ReportReward(Address author, string rewardType, UInt256 rewardValue)
    {
        // Not tracing rewards in blocktest mode
    }

    public void StartNewBlockTrace(Block block)
    {
        // No-op: we write continuously to the same stream across all blocks
    }

    public ITxTracer StartNewTxTrace(Transaction? tx)
    {
        _currentTxTracer = new GethLikeTxFileTracer(WriteTraceEntry, _options);
        return _currentTxTracer;
    }

    public void EndTxTrace()
    {
        if (_currentTxTracer == null) return;

        try
        {
            var trace = _currentTxTracer.BuildResult();

            // Write final summary line for this transaction
            using var stream = new MemoryStream();
            using var writer = new Utf8JsonWriter(stream);

            writer.WriteStartObject();
            writer.WritePropertyName("output");
            writer.WriteStringValue(trace.ReturnValue.ToHexString(true));
            writer.WritePropertyName("gasUsed");
            writer.WriteStringValue($"0x{trace.Gas:x}");
            writer.WriteEndObject();

            writer.Flush();
            _output.WriteLine(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
        }
        finally
        {
            _currentTxTracer = null;
        }
    }

    public void EndBlockTrace()
    {
        // No-op: we don't separate blocks in the output
        // All transactions are written continuously to stderr
    }

    private void WriteTraceEntry(GethTxFileTraceEntry entry)
    {
        if (entry is null) return;

        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);

        // Write trace entry (same format as GethLikeTxTraceJsonLinesConverter)
        writer.WriteStartObject();

        writer.WritePropertyName("pc");
        writer.WriteNumberValue(entry.ProgramCounter);

        writer.WritePropertyName("op");
        writer.WriteNumberValue((byte)entry.OpcodeRaw);

        writer.WritePropertyName("gas");
        writer.WriteStringValue($"0x{entry.Gas:x}");

        writer.WritePropertyName("gasCost");
        writer.WriteStringValue($"0x{entry.GasCost:x}");

        writer.WritePropertyName("memSize");
        writer.WriteNumberValue(entry.MemorySize ?? 0UL);

        if ((entry.Memory?.Length ?? 0) != 0)
        {
            var memory = string.Concat(entry.Memory);
            writer.WritePropertyName("memory");
            writer.WriteStringValue($"0x{memory}");
        }

        if (entry.Stack is not null)
        {
            writer.WritePropertyName("stack");
            writer.WriteStartArray();
            foreach (var s in entry.Stack)
                writer.WriteStringValue(s);
            writer.WriteEndArray();
        }

        writer.WritePropertyName("depth");
        writer.WriteNumberValue(entry.Depth);

        writer.WritePropertyName("refund");
        writer.WriteNumberValue(entry.Refund ?? 0L);

        writer.WritePropertyName("opName");
        writer.WriteStringValue(entry.Opcode);

        if (entry.Error is not null)
        {
            writer.WritePropertyName("error");
            writer.WriteStringValue(entry.Error);
        }

        writer.WriteEndObject();
        writer.Flush();

        _output.WriteLine(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
    }

    public void Dispose()
    {
        // Don't dispose Console.Error, but flush it
        _output.Flush();
    }
}
