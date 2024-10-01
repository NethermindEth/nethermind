// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using static Nethermind.Evm.CodeAnalysis.IL.ILCompiler;

namespace Nethermind.Evm.CodeAnalysis.IL;

/// <summary>
/// Provides
/// </summary>
public static class IlAnalyzer
{
    private static Dictionary<Type, InstructionChunk> _patterns = new Dictionary<Type, InstructionChunk>();
    internal static void AddPattern(InstructionChunk handler)
    {
        lock (_patterns)
        {
            _patterns[handler.GetType()] = handler;
        }
    }
    internal static void AddPattern<T>() where T : InstructionChunk
    {
        var handler = Activator.CreateInstance<T>();
        lock (_patterns)
        {
            _patterns[typeof(T)] = handler;
        }
    }
    internal static T GetPatternHandler<T>() where T : InstructionChunk
    {
        lock (_patterns)
        {
            return (T)_patterns[typeof(T)];
        }
    }

    public static void Initialize()
    {
        Type[] InstructionChunks = typeof(InstructionChunk).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(InstructionChunk))).ToArray();
        foreach (var chunkType in InstructionChunks)
        {
            _patterns[chunkType] = (InstructionChunk)Activator.CreateInstance(chunkType);
        }
    }

    /// <summary>
    /// Starts the analyzing in a background task and outputs the value in the <paramref name="codeInfo"/>.
    /// </summary> thou
    /// <param name="codeInfo">The destination output.</param>
    internal static Task StartAnalysis(CodeInfo codeInfo, IlInfo.ILMode mode, ILogger logger)
    {
        if(logger.IsInfo) logger.Info($"Starting IL-EVM analysis of code {codeInfo.CodeHash}");
        return Task.Run(() => Analysis(codeInfo, mode, logger));
    }

    public static (OpcodeInfo[], byte[][]) StripByteCode(ReadOnlySpan<byte> machineCode)
    {
        OpcodeInfo[] opcodes = new OpcodeInfo[machineCode.Length];
        List<byte[]> data = new List<byte[]>();
        int j = 0;
        for (ushort i = 0; i < machineCode.Length; i++, j++)
        {
            Instruction opcode = (Instruction)machineCode[i];
            int? argsIndex = null;
            ushort pc = i;
            if (opcode is > Instruction.PUSH0 and <= Instruction.PUSH32)
            {
                ushort immediatesCount = opcode - Instruction.PUSH0;
                data.Add(machineCode.SliceWithZeroPadding((UInt256)i + 1, immediatesCount, PadDirection.Left).ToArray());
                argsIndex = data.Count - 1;
                i += immediatesCount;
            }
            opcodes[j] = new OpcodeInfo(pc, opcode, argsIndex);
        }
        return (opcodes[..j], data.ToArray());
    }

    /// <summary>
    /// For now, return null always to default to EVM.
    /// </summary>
    private static void Analysis(CodeInfo codeInfo, IlInfo.ILMode mode, ILogger logger)
    {
        ReadOnlyMemory<byte> machineCode = codeInfo.MachineCode;

        static void SegmentCode(CodeInfo codeInfo, (OpcodeInfo[], byte[][]) codeData, IlInfo ilinfo)
        {
            if (codeData.Item1.Length == 0)
            {
                return;
            }

            string GenerateName(Range segmentRange) => $"ILEVM_PRECOMPILED_({codeInfo.CodeHash.ToShortString()})[{segmentRange.Start}..{segmentRange.End}]";

            int[] statefulOpcodeindex = new int[1 + (codeData.Item1.Length / 5)];

            int j = 0;
            for (int i = 0; i < codeData.Item1.Length; i++)
            {
                if (codeData.Item1[i].Operation.IsStateful())
                {
                    statefulOpcodeindex[j++] = i;
                }
            }

            for (int i = -1; i <= j; i++)
            {
                int start = i == -1 ? 0 : statefulOpcodeindex[i] + 1;
                int end = i == j - 1 || i + 1 == statefulOpcodeindex.Length ? codeData.Item1.Length : statefulOpcodeindex[i + 1];
                if (start > end)
                {
                    continue;
                }

                var segment = codeData.Item1[start..end];
                if (segment.Length == 0)
                {
                    continue;
                }
                var firstOp = segment[0];
                var lastOp = segment[^1];
                var segmentName = GenerateName(firstOp.ProgramCounter..(lastOp.ProgramCounter + lastOp.Metadata.AdditionalBytes));

                ilinfo.Segments.GetOrAdd((ushort)segment[0].ProgramCounter, CompileSegment(segmentName, segment, codeData.Item2));
            }

            ilinfo.Mode |= IlInfo.ILMode.SubsegmentsCompiling;
        }

        static void CheckPatterns(ReadOnlyMemory<byte> machineCode, IlInfo ilinfo)
        {
            var (strippedBytecode, data) = StripByteCode(machineCode.Span);
            foreach ((Type _, InstructionChunk chunkHandler) in _patterns)
            {
                for (int i = 0; i < strippedBytecode.Length - chunkHandler.Pattern.Length + 1; i++)
                {
                    bool found = true;
                    for (int j = 0; j < chunkHandler.Pattern.Length && found; j++)
                    {
                        found = ((byte)strippedBytecode[i + j].Operation == chunkHandler.Pattern[j]);
                    }

                    if (found)
                    {
                        ilinfo.Chunks.GetOrAdd((ushort)strippedBytecode[i].ProgramCounter, chunkHandler);
                        i += chunkHandler.Pattern.Length - 1;
                    }
                }
            }
            ilinfo.Mode |= IlInfo.ILMode.PatternMatching;
        }

        switch (mode)
        {
            case IlInfo.ILMode.PatternMatching:
                if(logger.IsInfo) logger.Info($"Analyzing patterns of code {codeInfo.CodeHash}");
                CheckPatterns(machineCode, codeInfo.IlInfo);
                break;
            case IlInfo.ILMode.SubsegmentsCompiling:
                if(logger.IsInfo) logger.Info($"Precompiling of segments of code {codeInfo.CodeHash}");
                SegmentCode(codeInfo, StripByteCode(machineCode.Span), codeInfo.IlInfo);
                break;
        }
    }
}
