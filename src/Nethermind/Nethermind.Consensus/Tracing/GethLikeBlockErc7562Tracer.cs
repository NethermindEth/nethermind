// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.GethStyle.Custom;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Call;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.Consensus.Tracing;

internal sealed class GethLikeBlockErc7562Tracer : IBlockTracer<GethLikeTxTrace>, IDisposable
{
    internal const string TracerName = "erc7562Tracer";
    private readonly IBlockTracer<GethLikeTxTrace> _inner;
    private readonly IWorldState _worldState;
    private readonly ISpecProvider _specProvider;
    private bool _failOnCodeDepositOutOfGas;
    private readonly Hash256? _txHash;
    private readonly bool[] _ignored;
    private readonly List<Collector> _collectors = [];
    private IReadOnlyCollection<GethLikeTxTrace>? _results;
    private bool _released;

    internal GethLikeBlockErc7562Tracer(GethTraceOptions options, IWorldState worldState, ISpecProvider specProvider)
    {
        (bool withLog, bool[] ignored) = ParseConfig(options.TracerConfig);
        _ignored = ignored;
        _worldState = worldState;
        _specProvider = specProvider;
        _txHash = options.TxHash;
        JsonElement config = JsonSerializer.SerializeToElement(new { withLog });
        _inner = new GethLikeBlockCallTracer(options.TxHash, (block, tx) =>
            new NativeCallTracer(tx, specProvider.GetSpec(block.Header), options with { Tracer = NativeCallTracer.CallTracer, TracerConfig = config }));
    }

    public bool IsTracingRewards => false;
    public void ReportReward(Address author, string rewardType, UInt256 rewardValue) => _inner.ReportReward(author, rewardType, rewardValue);
    public void StartNewBlockTrace(Block block)
    {
        ReleaseResults();
        _results = null;
        _released = false;
        _collectors.Clear();
        _failOnCodeDepositOutOfGas = _specProvider.GetSpec(block.Header).FailOnOutOfGasCodeDeposit;
        _inner.StartNewBlockTrace(block);
    }
    public ITxTracer StartNewTxTrace(Transaction? tx)
    {
        ITxTracer native = _inner.StartNewTxTrace(tx);
        if (tx is null || _txHash is not null && tx.Hash != _txHash) return native;
        Collector collector = new(_worldState, _ignored, _failOnCodeDepositOutOfGas);
        _collectors.Add(collector);
        return new CompositeTxTracer(native, collector);
    }
    public void EndTxTrace() => _inner.EndTxTrace();
    public void EndBlockTrace() => _inner.EndBlockTrace();

    public IReadOnlyCollection<GethLikeTxTrace> BuildResult()
    {
        if (_results is not null) return _results;
        if (_released) return [];
        List<GethLikeTxTrace> results = new(_collectors.Count);
        try
        {
            int index = 0;
            foreach (GethLikeTxTrace trace in _inner.BuildResult())
            {
                if (trace.CustomTracerResult?.Value is not NativeCallTracerCallFrame root ||
                    index >= _collectors.Count || _collectors[index].Root is not { } metadata)
                    throw new InvalidDataException("incorrect number of top-level calls");
                Collector collector = _collectors[index++];
                ArrayBufferWriter<byte> buffer = new();
                using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { MaxDepth = 4096 }))
                {
                    Stack<(NativeCallTracerCallFrame Call, Frame Metadata, int Child)> work = new();
                    work.Push((root, metadata, -1));
                    while (work.TryPop(out (NativeCallTracerCallFrame Call, Frame Metadata, int Child) item))
                    {
                        if (item.Child == -1)
                        {
                            if (item.Call.Calls.Count != item.Metadata.Children.Count)
                                throw new InvalidDataException("erc7562Tracer call frame mismatch");
                            WriteFrame(writer, item.Call, item.Metadata, ReferenceEquals(item.Call, root), collector.Preimages);
                            if (item.Call.Calls.Count == 0)
                            {
                                writer.WriteEndObject();
                                continue;
                            }
                            writer.WritePropertyName("calls");
                            writer.WriteStartArray();
                            item.Child = 0;
                        }
                        if (item.Child < item.Call.Calls.Count)
                        {
                            work.Push((item.Call, item.Metadata, item.Child + 1));
                            work.Push((item.Call.Calls[item.Child], item.Metadata.Children[item.Child], -1));
                        }
                        else
                        {
                            writer.WriteEndArray();
                            writer.WriteEndObject();
                        }
                    }
                }
                using JsonDocument document = JsonDocument.Parse(buffer.WrittenMemory, new JsonDocumentOptions { MaxDepth = 4096 });
                results.Add(new GethLikeTxTrace { TxHash = trace.TxHash, CustomTracerResult = new GethLikeCustomTrace { Value = document.RootElement.Clone() } });
            }
            return _results = results;
        }
        finally
        {
            // Detached JSON must outlive the pooled native frames, including when composed in muxTracer.
            ReleaseResults();
        }
    }

    private void ReleaseResults()
    {
        if (_released) return;
        _released = true;
        foreach (GethLikeTxTrace trace in _inner.BuildResult()) trace.Dispose();
    }
    public void Dispose() => ReleaseResults();

    private static void WriteFrame(Utf8JsonWriter writer, NativeCallTracerCallFrame call, Frame frame, bool root, SortedSet<string> preimages)
    {
        writer.WriteStartObject();
        writer.WriteString("type", Enum.GetName(call.Type));
        writer.WriteString("from", call.From?.ToString());
        if (call.To is not null) writer.WriteString("to", call.To.ToString());
        writer.WriteString("gas", Quantity(call.Gas));
        writer.WriteString("gasUsed", Quantity(call.GasUsed));
        if (call.Value is { } value) writer.WriteString("value", value.ToHexString(skipLeadingZeros: true));
        WriteBytes(writer, "input", call.Input);
        if (call.Output is { Count: > 0 }) WriteBytes(writer, "output", call.Output);
        if (call.Error is not null) writer.WriteString("error", call.Error);
        if (call.RevertReason is not null) writer.WriteString("revertReason", call.RevertReason);
        if (call.Logs is { Count: > 0 })
        {
            writer.WritePropertyName("logs");
            writer.WriteStartArray();
            foreach (NativeCallTracerLogEntry log in call.Logs)
            {
                writer.WriteStartObject();
                writer.WriteString("address", log.Address.ToString());
                writer.WriteString("data", "0x" + Convert.ToHexStringLower(log.Data));
                writer.WritePropertyName("topics");
                writer.WriteStartArray();
                foreach (Hash256 topic in log.Topics) writer.WriteStringValue(topic.ToString());
                writer.WriteEndArray();
                writer.WriteString("index", Quantity(log.Index));
                writer.WriteString("position", Quantity(log.Position));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        writer.WritePropertyName("accessedSlots");
        writer.WriteStartObject();
        writer.WritePropertyName("reads");
        writer.WriteStartObject();
        foreach ((string slot, string readValue) in frame.Reads)
        {
            writer.WritePropertyName(slot);
            writer.WriteStartArray();
            writer.WriteStringValue(readValue);
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
        WriteCounts(writer, "writes", frame.Writes);
        WriteCounts(writer, "transientReads", frame.TransientReads);
        WriteCounts(writer, "transientWrites", frame.TransientWrites);
        writer.WriteEndObject();
        writer.WritePropertyName("extCodeAccessInfo");
        writer.WriteStartArray();
        foreach (Address address in frame.ExtCodeAccess) writer.WriteStringValue(address.ToString());
        writer.WriteEndArray();
        WriteCounts(writer, "usedOpcodes", frame.Opcodes);
        writer.WritePropertyName("contractSize");
        writer.WriteStartObject();
        foreach ((Address address, (int size, Instruction opcode)) in frame.ContractSizes)
        {
            writer.WritePropertyName(address.ToString());
            writer.WriteStartObject();
            writer.WriteNumber("contractSize", size);
            writer.WriteNumber("opcode", (byte)opcode);
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
        writer.WriteBoolean("outOfGas", !root && (frame.CodeDepositOutOfGas || call.Error is { } error &&
            (error is "out of gas" or "contract creation code storage out of gas" || error.StartsWith("out of gas:", StringComparison.Ordinal))));
        if (root && preimages.Count != 0)
        {
            writer.WritePropertyName("keccak");
            writer.WriteStartArray();
            foreach (string preimage in preimages) writer.WriteStringValue(preimage);
            writer.WriteEndArray();
        }
    }

    private static string Quantity(ulong value) => "0x" + value.ToString("x", CultureInfo.InvariantCulture);
    private static string Word(UInt256 value) => "0x" + Convert.ToHexStringLower(value.ToBigEndian());
    private static void WriteBytes(Utf8JsonWriter writer, string name, ArrayPoolList<byte>? bytes) =>
        writer.WriteString(name, bytes is null ? "0x" : "0x" + Convert.ToHexStringLower(bytes.AsReadOnlyMemory().Span));
    private static void WriteCounts(Utf8JsonWriter writer, string name, Dictionary<string, ulong> counts)
    {
        writer.WritePropertyName(name);
        writer.WriteStartObject();
        foreach ((string key, ulong count) in counts) writer.WriteNumber(key, count);
        writer.WriteEndObject();
    }

    private sealed class Frame
    {
        internal Instruction? LastOpcode;
        internal ulong? ReturnGas;
        internal bool CodeDepositOutOfGas;
        internal readonly List<Frame> Children = [];
        internal readonly Dictionary<string, string> Reads = [];
        internal readonly Dictionary<string, ulong> Writes = [];
        internal readonly Dictionary<string, ulong> TransientReads = [];
        internal readonly Dictionary<string, ulong> TransientWrites = [];
        internal readonly Dictionary<string, ulong> Opcodes = [];
        internal readonly List<Address> ExtCodeAccess = [];
        internal readonly Dictionary<Address, (int Size, Instruction Opcode)> ContractSizes = [];
    }

    private sealed class Collector : TxTracer, ITraceImplicitStop
    {
        private readonly IWorldState _worldState;
        private readonly bool[] _ignored;
        private readonly bool _failOnCodeDepositOutOfGas;
        private readonly Stack<Frame> _frames = new();
        private Instruction _opcode;
        private Instruction? _lastOpcode;
        private Address? _lastAddress;
        private Address _account = null!;
        private TraceMemory _memory;
        internal Frame? Root { get; private set; }
        internal SortedSet<string> Preimages { get; } = new(StringComparer.Ordinal);

        internal Collector(IWorldState worldState, bool[] ignored, bool failOnCodeDepositOutOfGas)
        {
            _worldState = worldState;
            _ignored = ignored;
            _failOnCodeDepositOutOfGas = failOnCodeDepositOutOfGas;
            IsTracingActions = IsTracingInstructions = IsTracingStack = IsTracingMemory = true;
        }
        public override void ReportAction(ulong gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false)
        {
            Frame frame = new();
            if (_frames.TryPeek(out Frame? parent)) parent.Children.Add(frame);
            else Root = frame;
            _frames.Push(frame);
        }
        private void Exit() { if (_frames.Count > 1) _frames.Pop(); }
        public override void ReportActionEnd(ulong gas, ReadOnlyMemory<byte> output) => Exit();
        public override void ReportActionEnd(ulong gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode)
        {
            Frame frame = _frames.Peek();
            // Frontier keeps CREATE successful when code deposit fails; Geth still marks the ERC exit out of gas.
            frame.CodeDepositOutOfGas = !_failOnCodeDepositOutOfGas && frame.ReturnGas is { } remaining
                && remaining < (ulong)deployedCode.Length * GasCostOf.CodeDeposit;
            Exit();
        }
        public override void ReportOperationRemainingGas(ulong gas)
        {
            Frame frame = _frames.Peek();
            if (frame.LastOpcode == Instruction.RETURN) frame.ReturnGas = gas;
        }
        public override void ReportActionRevert(ulong gas, ReadOnlyMemory<byte> output) => Exit();
        public override void ReportActionError(EvmExceptionType evmExceptionType) => Exit();
        public override void ReportSelfDestruct(Address address, UInt256 balance, Address refundAddress) => _frames.Peek().Children.Add(new());
        public override void ReportRejectedAction(ulong gas, ulong gasLeft, UInt256 value, Address from, Address? to,
            ReadOnlyMemory<byte> input, ExecutionType callType, EvmExceptionType error, bool isPrecompileCall = false) => _frames.Peek().Children.Add(new());
        public override void StartOperation(int pc, Instruction opcode, ulong gas, in ExecutionEnvironment env)
        {
            _opcode = opcode;
            _account = env.ExecutingAccount;
            Frame frame = _frames.Peek();
            frame.LastOpcode = opcode;
            frame.ReturnGas = null;
        }
        public override void SetOperationMemory(TraceMemory memoryTrace) => _memory = memoryTrace;
        public override void SetOperationStack(TraceStack stack)
        {
            if (!_frames.TryPeek(out Frame? frame)) return;
            if (_opcode is Instruction.RETURN or Instruction.REVERT) _lastOpcode = null;
            // Geth keeps this lookbehind across frame entry/exit, not on the per-call stack.
            if (_lastOpcode is { } previous)
            {
                if (IsExt(previous) && _lastAddress is { } address && !(previous == Instruction.EXTCODESIZE && _opcode == Instruction.ISZERO))
                    frame.ExtCodeAccess.Add(address);
                if (previous == Instruction.GAS && !IsCall(_opcode)) Increment(frame.Opcodes, Quantity((byte)Instruction.GAS));
            }
            if (_opcode != Instruction.GAS && !_ignored[(byte)_opcode]) Increment(frame.Opcodes, Quantity((byte)_opcode));
            if ((IsExt(_opcode) || IsCall(_opcode)) && stack.Count > (IsExt(_opcode) ? 0 : 1))
            {
                Address address = stack.PeekAddress(IsExt(_opcode) ? 0 : 1);
                if (!frame.ContractSizes.ContainsKey(address))
                    frame.ContractSizes.Add(address, (_worldState.GetCode(address)?.Length ?? 0, _opcode));
            }
            if (_opcode is Instruction.SLOAD or Instruction.SSTORE or Instruction.TLOAD or Instruction.TSTORE && stack.Count >= 1)
            {
                UInt256 slot = stack.PeekUInt256(0);
                string key = Word(slot);
                if (_opcode == Instruction.SLOAD)
                {
                    if (!frame.Reads.ContainsKey(key) && !frame.Writes.ContainsKey(key))
                    {
                        _worldState.Get(new StorageCell(_account, slot), out UInt256 value);
                        frame.Reads.Add(key, Word(value));
                    }
                }
                else Increment(_opcode == Instruction.SSTORE ? frame.Writes : _opcode == Instruction.TLOAD ? frame.TransientReads : frame.TransientWrites, key);
            }
            if (_opcode == Instruction.KECCAK256 && stack.Count >= 2)
            {
                ulong offset = stack.PeekUInt256(0).u0;
                ulong length = stack.PeekUInt256(1).u0;
                if (offset <= int.MaxValue && length <= int.MaxValue && offset + length <= int.MaxValue &&
                    (offset + length <= _memory.Size || offset + length - _memory.Size <= MemorySizes.MiB))
                    Preimages.Add("0x" + Convert.ToHexStringLower(_memory.Slice((int)offset, (int)length, limit: false)));
            }
            _lastOpcode = _opcode;
            _lastAddress = IsExt(_opcode) && stack.Count != 0 ? stack.PeekAddress(0) : null;
        }
        private static bool IsExt(Instruction op) => op is Instruction.EXTCODEHASH or Instruction.EXTCODESIZE or Instruction.EXTCODECOPY;
        private static bool IsCall(Instruction op) => op is Instruction.CALL or Instruction.CALLCODE or Instruction.DELEGATECALL or Instruction.STATICCALL;
        private static void Increment(Dictionary<string, ulong> counts, string key) => counts[key] = counts.GetValueOrDefault(key) + 1;
    }

    private static (bool WithLog, bool[] Ignored) ParseConfig(JsonElement? config)
    {
        bool withLog = false;
        bool[]? ignored = null;
        if (config is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } value)
        {
            if (value.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"json: cannot unmarshal {JsonType(value)} into Go value of type native.erc7562TracerConfig");
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Null)
                {
                    if (property.Name.Equals("ignoredOpcodes", StringComparison.OrdinalIgnoreCase)) ignored = null;
                    continue;
                }
                if (property.Name.Equals("withLog", StringComparison.OrdinalIgnoreCase))
                {
                    if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw ConfigError(property.Value, "withLog", "bool");
                    withLog = property.Value.GetBoolean();
                }
                else if (property.Name.Equals("stackTopItemsSize", StringComparison.OrdinalIgnoreCase))
                {
                    if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt64(out long size)) throw ConfigError(property.Value, "stackTopItemsSize", "int");
                    // Upstream panics while allocating a negative slice; reject it rather than reproduce a crash.
                    if (size < 0) throw new InvalidDataException("stackTopItemsSize must not be negative");
                }
                else if (property.Name.Equals("ignoredOpcodes", StringComparison.OrdinalIgnoreCase))
                {
                    if (property.Value.ValueKind != JsonValueKind.Array) throw ConfigError(property.Value, "ignoredOpcodes", "[]hexutil.Uint64");
                    ignored = new bool[256];
                    foreach (JsonElement opcode in property.Value.EnumerateArray())
                    {
                        if (opcode.ValueKind != JsonValueKind.String)
                            throw new InvalidDataException("json: cannot unmarshal non-string into Go struct field erc7562TracerConfig.ignoredOpcodes of type hexutil.Uint64");
                        string text = opcode.GetString()!;
                        string? error = !text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? "hex string without 0x prefix"
                            : text.Length == 2 ? "hex string \"0x\"" : text.Length > 3 && text[2] == '0' ? "hex number with leading zero digits"
                            : text.Length > 18 ? "hex number > 64 bits" : null;
                        if (error is not null || !ulong.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong number))
                            throw new InvalidDataException($"json: cannot unmarshal {error ?? "invalid hex string"} into Go struct field erc7562TracerConfig.ignoredOpcodes of type hexutil.Uint64");
                        ignored[unchecked((byte)number)] = true;
                    }
                }
            }
        }
        if (ignored is null)
        {
            ignored = new bool[256];
            for (int op = (int)Instruction.PUSH0; op <= (int)Instruction.SWAP16; op++) ignored[op] = true;
            foreach (Instruction op in new[] { Instruction.POP, Instruction.ADD, Instruction.SUB, Instruction.MUL, Instruction.DIV, Instruction.EQ, Instruction.LT, Instruction.GT, Instruction.SLT, Instruction.SGT, Instruction.SHL, Instruction.SHR, Instruction.AND, Instruction.OR, Instruction.NOT, Instruction.ISZERO })
                ignored[(byte)op] = true;
        }
        return (withLog, ignored);
    }
    private static InvalidDataException ConfigError(JsonElement value, string field, string type) =>
        new($"json: cannot unmarshal {JsonType(value)} into Go struct field erc7562TracerConfig.{field} of type {type}");
    private static string JsonType(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Array => "array",
        JsonValueKind.Object => "object",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        _ => "bool"
    };
}
