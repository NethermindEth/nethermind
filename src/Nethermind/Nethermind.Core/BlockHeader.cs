// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Core;

public static class Out
{
    public static readonly long TargetBlockNumber = long.TryParse(Environment.GetEnvironmentVariable("TARGET_BLOCK_NUMBER"), out long tbn) ? tbn : -2;
    public static readonly bool TraceShowStackTrace = Environment.GetEnvironmentVariable("TRACE_SHOW_STACKTRACE") == "true";
    public static readonly bool TraceShowBurn = Environment.GetEnvironmentVariable("TRACE_SHOW_BURN") == "true";
    public static readonly bool TraceShowOpcodes = Environment.GetEnvironmentVariable("TRACE_SHOW_OPCODES") == "true";
    public static readonly bool TraceShowDeepstate = Environment.GetEnvironmentVariable("TRACE_SHOW_DEEPSTATE") == "true";
    public static readonly bool TraceShowArbosRead = Environment.GetEnvironmentVariable("TRACE_SHOW_ARBOS_READ") == "true";
    public static readonly bool TraceShowStateRootChange = Environment.GetEnvironmentVariable("TRACE_SHOW_STATE_ROOT_CHANGE") == "true";

    public static long CurrentBlockNumber { get; set; }
    public static long CurrentTransactionIndex { get; set; }
    public static bool IsTargetBlock => CurrentBlockNumber == TargetBlockNumber;

    public static void Reset()
    {
        CurrentBlockNumber = -1;
        CurrentTransactionIndex = -1;
    }

    public static void DumpEnvironmentVariables()
    {
        Console.WriteLine($"{nameof(TraceShowStackTrace)}: {TraceShowStackTrace}");
        Console.WriteLine($"{nameof(TraceShowBurn)}: {TraceShowBurn}");
        Console.WriteLine($"{nameof(TraceShowOpcodes)}: {TraceShowOpcodes}");
        Console.WriteLine($"{nameof(TraceShowDeepstate)}: {TraceShowDeepstate}");
        Console.WriteLine($"{nameof(TraceShowArbosRead)}: {TraceShowArbosRead}");
        Console.WriteLine($"{nameof(TraceShowStateRootChange)}: {TraceShowStateRootChange}");
    }

    public static void LogAlways(string log)
    {
        if (TraceShowStackTrace)
            Console.WriteLine(GetCallStackString());

        Console.WriteLine(log);
    }

    public static void Log(string log)
    {
        if (!IsTargetBlock)
            return;

        if (TraceShowStackTrace)
            Console.WriteLine($"b={CurrentBlockNumber}, t={CurrentTransactionIndex}, {GetCallStackString()}");

        Console.WriteLine($"b={CurrentBlockNumber}, t={CurrentTransactionIndex} {log}");
    }

    public static void LogFast(string log)
    {
        if (!IsTargetBlock)
            return;

        Console.WriteLine($"b={CurrentBlockNumber}, t={CurrentTransactionIndex} {log}");
    }

    public static void Log(string scope, string key, string value)
    {
        if (!IsTargetBlock)
            return;

        if (TraceShowStackTrace)
            Console.WriteLine($"b={CurrentBlockNumber}, t={CurrentTransactionIndex}, {GetCallStackString()}");

        Console.WriteLine($"b={CurrentBlockNumber}, t={CurrentTransactionIndex}, s={scope}: {key} {value}");
    }

    private static List<StackFrame> GetCallStack(int skipFrames = 2, int maxDepth = 20)
    {
        var stackTrace = new StackTrace(skipFrames, fNeedFileInfo: true);
        var frames = new List<StackFrame>();

        var frameCount = Math.Min(stackTrace.FrameCount, maxDepth);

        for (int i = 0; i < frameCount; i++)
        {
            var frame = stackTrace.GetFrame(i);
            if (frame == null) continue;

            var method = frame.GetMethod();
            var fileName = frame.GetFileName() ?? string.Empty;

            // Extract just the filename without full path
            if (!string.IsNullOrEmpty(fileName))
                fileName = Path.GetFileName(fileName);

            var structureName = string.Empty;
            var methodName = method?.Name ?? "Unknown";

            if (method?.DeclaringType != null)
            {
                // Check if it's a method on a class/struct (not a static class method)
                var declaringType = method.DeclaringType;

                // Use the type name without namespace for compactness
                structureName = declaringType.IsNested
                    ? $"{declaringType.DeclaringType?.Name}.{declaringType.Name}"
                    : declaringType.Name;

                // Clean up compiler-generated names for async methods, lambdas, etc.
                if (methodName.Contains("<") && methodName.Contains(">"))
                {
                    // Extract the actual method name from compiler-generated names like <MethodName>b__0
                    var startIdx = methodName.IndexOf('<') + 1;
                    var endIdx = methodName.IndexOf('>');
                    if (startIdx < endIdx)
                        methodName = methodName.Substring(startIdx, endIdx - startIdx);
                }

                // Remove generic type parameters for readability
                if (structureName.Contains('`'))
                    structureName = structureName.Substring(0, structureName.IndexOf('`'));
            }

            frames.Add(new StackFrame
            {
                File = fileName,
                LineNumber = frame.GetFileLineNumber(),
                StructureName = structureName,
                MethodName = methodName
            });
        }

        // Reverse to show in chronological order (oldest to newest)
        frames.Reverse();

        return frames;
    }

    private static string GetCallStackString(int skipFrames = 3)
    {
        var frames = GetCallStack(skipFrames);
        if (frames.Count == 0) return string.Empty;

        var sb = new StringBuilder(frames.Count * 30);

        for (int i = 0; i < frames.Count; i++)
        {
            if (i > 0)
                sb.Append(" → ");

            var frame = frames[i];

            // Format: file:line [ClassName.]MethodName
            if (!string.IsNullOrEmpty(frame.File))
            {
                sb.Append(frame.File);
                sb.Append(':');
                sb.Append(frame.LineNumber);
                sb.Append(' ');
            }

            if (!string.IsNullOrEmpty(frame.StructureName))
            {
                sb.Append(frame.StructureName);
                sb.Append('.');
            }
            sb.Append(frame.MethodName);
        }

        return sb.ToString();
    }

    private record StackFrame
    {
        public string File { get; init; } = string.Empty;
        public int LineNumber { get; init; }
        public string StructureName { get; init; } = string.Empty;
        public string MethodName { get; init; } = string.Empty;
    }
}

public static class ProcessingMetrics
{
    public static long ArbOsGetDurationNanos { get; set; }
    public static long ArbOsSetDurationNanos { get; set; }
    public static long SLoadDurationNanos { get; set; }
    public static long SStoreDurationNanos { get; set; }

    public static void Reset()
    {
        ArbOsGetDurationNanos = 0;
        ArbOsSetDurationNanos = 0;
        SLoadDurationNanos = 0;
        SStoreDurationNanos = 0;
    }
}

[DebuggerDisplay("{Hash} ({Number})")]
public class BlockHeader
{
    internal BlockHeader() { }

    public BlockHeader(
        Hash256 parentHash,
        Hash256 unclesHash,
        Address beneficiary,
        in UInt256 difficulty,
        long number,
        long gasLimit,
        ulong timestamp,
        byte[] extraData,
        ulong? blobGasUsed = null,
        ulong? excessBlobGas = null,
        Hash256? parentBeaconBlockRoot = null,
        Hash256? requestsHash = null)
    {
        ParentHash = parentHash;
        UnclesHash = unclesHash;
        Beneficiary = beneficiary;
        Difficulty = difficulty;
        Number = number;
        GasLimit = gasLimit;
        Timestamp = timestamp;
        ExtraData = extraData;
        ParentBeaconBlockRoot = parentBeaconBlockRoot;
        RequestsHash = requestsHash;
        BlobGasUsed = blobGasUsed;
        ExcessBlobGas = excessBlobGas;
    }

    public virtual long GenesisBlockNumber => 0;
    public bool IsGenesis => Number == GenesisBlockNumber;
    public Hash256? ParentHash { get; set; }
    public Hash256? UnclesHash { get; set; }
    public Address? Author { get; set; }
    public Address? Beneficiary { get; set; }
    public Address? GasBeneficiary => Author ?? Beneficiary;

    public Hash256? StateRoot
    {
        get => _stateRoot;
        set
        {
            if (Out.IsTargetBlock)
                Out.Log($"block set stateRoot={value}");
            _stateRoot = value;
        }
    }

    public Hash256? TxRoot { get; set; }
    public Hash256? ReceiptsRoot { get; set; }
    public Bloom? Bloom { get; set; }
    public UInt256 Difficulty;
    public long Number { get; set; }
    public long GasUsed { get; set; }
    public long GasLimit { get; set; }
    public ulong Timestamp { get; set; }
    public DateTime TimestampDate => DateTimeOffset.FromUnixTimeSeconds((long)Timestamp).LocalDateTime;
    public byte[] ExtraData { get; set; } = [];
    public Hash256? MixHash { get; set; }
    public Hash256? Random => MixHash;
    public ulong Nonce { get; set; }
    public Hash256? Hash { get; set; }
    public UInt256? TotalDifficulty { get; set; }
    public byte[]? AuRaSignature { get; set; }
    public long? AuRaStep { get; set; }
    public UInt256 BaseFeePerGas;
    private Hash256? _stateRoot;
    public Hash256? WithdrawalsRoot { get; set; }
    public Hash256? ParentBeaconBlockRoot { get; set; }
    public Hash256? RequestsHash { get; set; }
    public ulong? BlobGasUsed { get; set; }
    public ulong? ExcessBlobGas { get; set; }
    public bool HasBody => (TxRoot is not null && TxRoot != Keccak.EmptyTreeHash)
                           || (UnclesHash is not null && UnclesHash != Keccak.OfAnEmptySequenceRlp)
                           || (WithdrawalsRoot is not null && WithdrawalsRoot != Keccak.EmptyTreeHash);

    public bool HasTransactions => (TxRoot is not null && TxRoot != Keccak.EmptyTreeHash);

    public bool IsPostMerge { get; set; }

    public string ToString(string indent)
    {
        StringBuilder builder = new();
        builder.AppendLine($"{indent}Hash: {Hash}");
        builder.AppendLine($"{indent}Number: {Number}");
        builder.AppendLine($"{indent}Parent: {ParentHash}");
        builder.AppendLine($"{indent}Beneficiary: {Beneficiary}");
        builder.AppendLine($"{indent}Gas Limit: {GasLimit}");
        builder.AppendLine($"{indent}Gas Used: {GasUsed}");
        builder.AppendLine($"{indent}Timestamp: {Timestamp}");
        builder.AppendLine($"{indent}Extra Data: {ExtraData.ToHexString()}");
        builder.AppendLine($"{indent}Difficulty: {Difficulty}");
        builder.AppendLine($"{indent}Mix Hash: {MixHash}");
        builder.AppendLine($"{indent}Nonce: {Nonce}");
        builder.AppendLine($"{indent}Uncles Hash: {UnclesHash}");
        builder.AppendLine($"{indent}Tx Root: {TxRoot}");
        builder.AppendLine($"{indent}Receipts Root: {ReceiptsRoot}");
        builder.AppendLine($"{indent}State Root: {StateRoot}");
        builder.AppendLine($"{indent}BaseFeePerGas: {BaseFeePerGas}");
        if (WithdrawalsRoot is not null)
        {
            builder.AppendLine($"{indent}WithdrawalsRoot: {WithdrawalsRoot}");
        }
        if (ParentBeaconBlockRoot is not null)
        {
            builder.AppendLine($"{indent}ParentBeaconBlockRoot: {ParentBeaconBlockRoot}");
        }
        if (BlobGasUsed is not null || ExcessBlobGas is not null)
        {
            builder.AppendLine($"{indent}BlobGasUsed: {BlobGasUsed}");
            builder.AppendLine($"{indent}ExcessBlobGas: {ExcessBlobGas}");
        }
        builder.AppendLine($"{indent}IsPostMerge: {IsPostMerge}");
        builder.AppendLine($"{indent}TotalDifficulty: {TotalDifficulty}");
        if (RequestsHash is not null)
        {
            builder.AppendLine($"{indent}RequestsHash: {RequestsHash}");
        }

        return builder.ToString();
    }

    public override string ToString() => ToString(string.Empty);

    public string ToString(Format format) => format switch
    {
        Format.Full => ToString(string.Empty),
        Format.FullHashAndNumber => Hash is null ? $"{Number} null" : $"{Number} ({Hash})",
        _ => Hash is null ? $"{Number} null" : $"{Number} ({Hash.ToShortString()})",
    };

    [Todo(Improve.Refactor, "Use IFormattable here")]
    public enum Format
    {
        Full,
        Short,
        FullHashAndNumber
    }

    public BlockHeader Clone()
    {
        BlockHeader header = (BlockHeader)MemberwiseClone();
        header.Bloom = Bloom?.Clone() ?? new Bloom();
        return header;
    }
}
