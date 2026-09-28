// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.GethStyle.Custom;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Call;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.Consensus.Tracing;

internal sealed class GethLikeBlockFlatCallTracer : IBlockTracer<GethLikeTxTrace>, IDisposable
{
    internal const string TracerName = "flatCallTracer";
    private readonly IBlockTracer<GethLikeTxTrace> _inner;
    private readonly IReleaseSpec _spec;
    private readonly bool _includePrecompiles;
    private readonly bool _convertParityErrors;
    private readonly List<(Hash256? BlockHash, ulong BlockNumber, Hash256? TxHash, int Position)> _contexts = [];
    private IReadOnlyCollection<GethLikeTxTrace>? _results;
    private bool _released;

    internal GethLikeBlockFlatCallTracer(GethTraceOptions options, IReleaseSpec spec, bool isTraceCall)
    {
        (_includePrecompiles, _convertParityErrors) = ParseConfig(options.TracerConfig);
        _spec = spec;
        _inner = new GethLikeBlockNativeTracer(options.TxHash, (block, tx) =>
        {
            _contexts.Add(isTraceCall ? (null, 0, null, 0)
                : (block.Hash, block.Number, tx.Hash, Array.IndexOf(block.Transactions, tx)));
            return new NativeCallTracer(tx, spec, options with { Tracer = NativeCallTracer.CallTracer, TracerConfig = null });
        });
    }

    private static (bool IncludePrecompiles, bool ConvertParityErrors) ParseConfig(JsonElement? config)
    {
        if (config is null || config.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return default;
        if (config.Value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"json: cannot unmarshal {JsonType(config.Value)} into Go value of type native.flatCallTracerConfig");
        bool include = false, convert = false;
        foreach (JsonProperty property in config.Value.EnumerateObject())
        {
            bool isInclude = property.Name.Equals("includePrecompiles", StringComparison.OrdinalIgnoreCase);
            if (!isInclude && !property.Name.Equals("convertParityErrors", StringComparison.OrdinalIgnoreCase)) continue;
            if (property.Value.ValueKind == JsonValueKind.Null) continue;
            if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException($"json: cannot unmarshal {JsonType(property.Value)} into Go struct field flatCallTracerConfig.{(isInclude ? "includePrecompiles" : "convertParityErrors")} of type bool");
            if (isInclude) include = property.Value.GetBoolean();
            else convert = property.Value.GetBoolean();
        }
        return (include, convert);
    }

    private static string JsonType(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Array => "array",
        JsonValueKind.Object => "object",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        _ => "bool"
    };

    /// <inheritdoc/>
    public bool IsTracingRewards => false;

    /// <inheritdoc/>
    public void ReportReward(Address author, string rewardType, UInt256 rewardValue) => _inner.ReportReward(author, rewardType, rewardValue);

    /// <inheritdoc/>
    public void StartNewBlockTrace(Block block)
    {
        ReleaseResults();
        _results = null;
        _released = false;
        _contexts.Clear();
        _inner.StartNewBlockTrace(block);
    }

    /// <inheritdoc/>
    public ITxTracer StartNewTxTrace(Transaction? tx) => _inner.StartNewTxTrace(tx);

    /// <inheritdoc/>
    public void EndTxTrace() => _inner.EndTxTrace();

    /// <inheritdoc/>
    public void EndBlockTrace() => _inner.EndBlockTrace();

    /// <inheritdoc/>
    public IReadOnlyCollection<GethLikeTxTrace> BuildResult()
    {
        if (_results is not null) return _results;
        if (_released) return [];
        List<GethLikeTxTrace> results = new(_contexts.Count);
        try
        {
            int index = 0;
            foreach (GethLikeTxTrace trace in _inner.BuildResult())
            {
                if (trace.CustomTracerResult?.Value is not NativeCallTracerCallFrame root)
                    throw new InvalidDataException("invalid number of calls");
                ArrayBufferWriter<byte> buffer = new();
                using (Utf8JsonWriter writer = new(buffer))
                {
                    writer.WriteStartArray();
                    Stack<(NativeCallTracerCallFrame Frame, int[] Path)> pending = new();
                    pending.Push((root, []));
                    while (pending.TryPop(out (NativeCallTracerCallFrame Frame, int[] Path) item))
                    {
                        int children = 0;
                        foreach (NativeCallTracerCallFrame child in item.Frame.Calls)
                            if (!Skip(child)) children++;
                        WriteFrame(writer, item.Frame, item.Path, children, _contexts[index]);
                        for (int i = item.Frame.Calls.Count - 1; i >= 0; i--)
                        {
                            NativeCallTracerCallFrame child = item.Frame.Calls[i];
                            if (Skip(child)) continue;
                            int[] path = new int[item.Path.Length + 1];
                            item.Path.CopyTo(path, 0);
                            path[^1] = --children;
                            pending.Push((child, path));
                        }
                    }
                    writer.WriteEndArray();
                }
                using JsonDocument document = JsonDocument.Parse(buffer.WrittenMemory);
                results.Add(new GethLikeTxTrace
                {
                    TxHash = trace.TxHash,
                    CustomTracerResult = new GethLikeCustomTrace { Value = document.RootElement.Clone() }
                });
                index++;
            }
            return _results = results;
        }
        finally
        {
            // The detached JSON must outlive the pooled call frames, including when nested in muxTracer.
            ReleaseResults();
        }
    }

    private bool Skip(NativeCallTracerCallFrame frame) => !_includePrecompiles
        && frame.Type is Instruction.CALL or Instruction.STATICCALL
        && frame.To is { } to && _spec.IsPrecompile(to);

    private void WriteFrame(Utf8JsonWriter writer, NativeCallTracerCallFrame frame, int[] path, int children,
        (Hash256? BlockHash, ulong BlockNumber, Hash256? TxHash, int Position) context)
    {
        bool create = frame.Type is Instruction.CREATE or Instruction.CREATE2;
        bool suicide = frame.Type == Instruction.SELFDESTRUCT;
        writer.WriteStartObject();
        writer.WritePropertyName("action");
        writer.WriteStartObject();
        if (suicide)
        {
            writer.WriteString("address", frame.From?.ToString());
            writer.WriteString("balance", Quantity(frame.Value ?? UInt256.Zero));
            writer.WriteString("refundAddress", frame.To?.ToString());
        }
        else
        {
            writer.WriteString(create ? "creationMethod" : "callType", frame.Type.ToString().ToLowerInvariant());
            writer.WriteString("from", frame.From?.ToString());
            writer.WriteString("gas", Quantity(frame.Gas));
            WriteBytes(writer, create ? "init" : "input", frame.Input);
            if (!create && frame.To is { } to) writer.WriteString("to", to.ToString());
            writer.WriteString("value", Quantity(frame.Value ?? UInt256.Zero));
        }
        writer.WriteEndObject();
        writer.WriteString("blockHash", context.BlockHash?.ToString());
        writer.WriteNumber("blockNumber", context.BlockNumber);
        if (!string.IsNullOrEmpty(frame.Error))
            writer.WriteString("error", _convertParityErrors ? ConvertError(frame.Error) : frame.Error);
        if (!suicide && (string.IsNullOrEmpty(frame.Error) || frame.Error == "execution reverted"))
        {
            writer.WritePropertyName("result");
            writer.WriteStartObject();
            if (create && frame.To is { } address) writer.WriteString("address", address.ToString());
            WriteBytes(writer, create ? "code" : "output", frame.Output);
            writer.WriteString("gasUsed", Quantity(frame.GasUsed));
            writer.WriteEndObject();
        }
        writer.WriteNumber("subtraces", children);
        writer.WritePropertyName("traceAddress");
        writer.WriteStartArray();
        foreach (int part in path) writer.WriteNumberValue(part);
        writer.WriteEndArray();
        writer.WriteString("transactionHash", context.TxHash?.ToString());
        writer.WriteNumber("transactionPosition", context.Position);
        writer.WriteString("type", suicide ? "suicide" : create ? "create" : "call");
        writer.WriteEndObject();
    }

    private static string Quantity(UInt256 value) => value.ToHexString(skipLeadingZeros: true);
    private static void WriteBytes(Utf8JsonWriter writer, string name, ArrayPoolList<byte>? value) =>
        writer.WriteString(name, value is null ? "0x" : "0x" + Convert.ToHexStringLower(value.AsReadOnlyMemory().Span));

    private static string ConvertError(string error) => error switch
    {
        "contract creation code storage out of gas" or "out of gas" or "gas uint64 overflow" or "max code size exceeded" => "Out of gas",
        "invalid jump destination" => "Bad jump destination",
        "execution reverted" => "Reverted",
        "return data out of bounds" => "Out of bounds",
        "stack limit reached 1024 (1023)" => "Out of stack",
        "precompiled failed" or "invalid input length" => "Built-in failed",
        _ when error.StartsWith("out of gas:", StringComparison.Ordinal) => "Out of gas",
        _ when error.StartsWith("invalid opcode:", StringComparison.Ordinal) => "Bad instruction",
        _ when error.StartsWith("stack underflow", StringComparison.Ordinal) => "Stack underflow",
        _ => error
    };

    private void ReleaseResults()
    {
        if (_released) return;
        _released = true;
        foreach (GethLikeTxTrace trace in _inner.BuildResult()) trace.Dispose();
    }

    /// <inheritdoc/>
    public void Dispose() => ReleaseResults();
}
