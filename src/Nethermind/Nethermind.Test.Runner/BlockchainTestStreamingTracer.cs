// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Text;
using System.Text.Json;
using Ethereum.Test.Base;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.Test.Runner;

/// <summary>
/// Streaming tracer for blockchain tests that writes all traces to stderr.
/// Compatible with go-ethereum's block test tracing output format.
/// Outputs consolidated traces across all blocks and transactions in a single stream.
/// </summary>
public class BlockchainTestStreamingTracer(GethTraceOptions options, Stream? output = null) : ITestBlockTracer, IDisposable
{
    private static readonly byte[] _newLine = Encoding.UTF8.GetBytes(Environment.NewLine);
    private readonly Stream _output = output ?? Console.OpenStandardError();
    private readonly GethTraceOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private GethLikeTxFileTracer? _currentTxTracer;

    // Track metrics for test end marker
    private int _transactionCount;
    private int _blockCount;
    private long _totalGasUsed;

    public bool IsTracingRewards => false;

    public void ReportReward(Address author, string rewardType, UInt256 rewardValue)
    {
        // Not tracing rewards in block test mode
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
        if (_currentTxTracer is null) return;

        try
        {
            GethLikeTxTrace? trace = _currentTxTracer.BuildResult();

            // Write the final summary line for this transaction
            using var writer = new Utf8JsonWriter(_output, new JsonWriterOptions { Indented = false });

            writer.WriteStartObject();
            writer.WritePropertyName("output");
            writer.WriteStringValue(trace.ReturnValue.ToHexString(true));
            writer.WritePropertyName("gasUsed");
            writer.WriteStringValue($"0x{trace.Gas:x}");
            writer.WriteEndObject();

            writer.Flush();

            _output.Write(_newLine);

            // Track metrics for end marker
            _transactionCount++;
            _totalGasUsed += trace.Gas;
        }
        finally
        {
            _currentTxTracer = null;
        }
    }

    public void EndBlockTrace()
    {
        // Track block count for end marker
        _blockCount++;
    }

    /// <summary>
    /// Writes a JSONL-compliant test end marker to the trace stream.
    /// This MUST be the last line written to stderr for the test.
    /// Format: {"testEnd":{"name":"...","pass":bool,"fork":"...","v":1,...}}
    /// </summary>
    public void TestFinished(string testName, bool pass, IReleaseSpec spec, TimeSpan? duration, Hash256? headStateRoot)
    {
        using var writer = new Utf8JsonWriter(_output, new JsonWriterOptions
        {
            Indented = false  // Critical: Single line for JSONL compliance
        });

        writer.WriteStartObject();
        writer.WritePropertyName("testEnd");
        writer.WriteStartObject();

        // Required fields
        writer.WriteString("name", testName);
        writer.WriteBoolean("pass", pass);
        writer.WriteString("fork", spec.ToString());
        writer.WriteNumber("v", 1);

        // Optional fields (only if available)
        if (duration.HasValue)
            writer.WriteNumber("d", Math.Round(duration.Value.TotalSeconds, 3));

        if (_totalGasUsed > 0)
            writer.WriteString("gasUsed", $"0x{_totalGasUsed:x}");

        if (_transactionCount > 0)
            writer.WriteNumber("txs", _transactionCount);

        if (_blockCount > 0)
            writer.WriteNumber("blocks", _blockCount);

        if (headStateRoot is not null)
            writer.WriteString("root", headStateRoot.ToString());

        writer.WriteEndObject(); // testEnd
        writer.WriteEndObject(); // root

        writer.Flush();

        // Write single line with newline - MUST be the last line in trace
        _output.Write(_newLine);
        _output.Flush(); // Critical: ensure written before process exit
    }

    private void WriteTraceEntry(GethTxFileTraceEntry entry)
    {
        using var writer = new Utf8JsonWriter(_output, new JsonWriterOptions { Indented = false });

        // Write trace entry (same format as GethLikeTxTraceJsonLinesConverter)
        writer.WriteStartObject();

        writer.WritePropertyName("pc");
        writer.WriteNumberValue(entry.ProgramCounter);

        writer.WritePropertyName("op");
        writer.WriteNumberValue((byte)entry.OpcodeRaw!);

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

        _output.Write(_newLine);
    }

    public void Dispose()
    {
        // Don't dispose of Console.Error, but flush it
        _output.Flush();
    }
}
