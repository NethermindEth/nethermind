// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.Config;
using Nethermind.Int256;
using Nethermind.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using static Nethermind.Evm.CodeAnalysis.IL.ILCompiler;
using IlevmMode = int;

[assembly : InternalsVisibleTo("Nethermind.Evm.Tests")]
[assembly : InternalsVisibleTo("Nethermind.Evm.Benchmarks")]
namespace Nethermind.Evm.CodeAnalysis.IL;

/// <summary>
/// Provides
/// </summary>
public static class IlAnalyzer
{
    public class AnalysisWork(CodeInfo codeInfo, IlevmMode mode)
    {
        public CodeInfo CodeInfo = codeInfo;
        public IlevmMode Mode = mode;
    }
    private static readonly ConcurrentQueue<AnalysisWork> _queue = new();

    public static void Enqueue(CodeInfo codeInfo, IlevmMode mode, IVMConfig config, ILogger logger)
    {
        _queue.Enqueue(new AnalysisWork(codeInfo, mode));
        if(config.AnalysisQueueMaxSize <= _queue.Count)
        {
            Task.Run(() => AnalyzeQueue(config, logger));
        }
    }

    private static void AnalyzeQueue(IVMConfig config, ILogger logger)
    {
        int itemsLeft = _queue.Count;
        while (itemsLeft-- > 0 && _queue.TryDequeue(out AnalysisWork worklet))
        {
            if (worklet.CodeInfo.IlInfo.IsBeingProcessed)
            {
                _queue.Enqueue(worklet);
            }
            else
            {
                worklet.CodeInfo.IlInfo.IsBeingProcessed = true;
                IlAnalyzer.Analyse(worklet.CodeInfo, worklet.Mode, config, logger);
                worklet.CodeInfo.IlInfo.IsBeingProcessed = false;
            }
        }
    }

    private static Dictionary<Type, IPatternChunk> _patterns = new Dictionary<Type, IPatternChunk>();
    internal static void AddPattern(IPatternChunk handler)
    {
        lock (_patterns)
        {
            _patterns[handler.GetType()] = handler;
        }
    }
    internal static void AddPattern<T>() where T : IPatternChunk
    {
        var handler = Activator.CreateInstance<T>();
        lock (_patterns)
        {
            _patterns[typeof(T)] = handler;
        }
    }
    internal static T GetPatternHandler<T>() where T : IPatternChunk
    {
        lock (_patterns)
        {
            return (T)_patterns[typeof(T)];
        }
    }

    public static void Initialize()
    {
        Type[] InstructionChunks = typeof(IPatternChunk).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(InstructionChunk))).ToArray();
        foreach (var chunkType in InstructionChunks)
        {
            _patterns[chunkType] = (IPatternChunk)Activator.CreateInstance(chunkType);
        }
    }

    public static (OpcodeInfo[], byte[][]) StripByteCode(ReadOnlySpan<byte> machineCode)
    {
        OpcodeInfo[] opcodes = new OpcodeInfo[machineCode.Length];
        List<byte[]> data = new List<byte[]>();
        int j = 0;
        for (int i = 0; i < machineCode.Length; i++, j++)
        {
            Instruction opcode = (Instruction)machineCode[i];
            int? argsIndex = null;
            int pc = i;
            if (opcode is > Instruction.PUSH0 and <= Instruction.PUSH32)
            {
                ushort immediatesCount = opcode - Instruction.PUSH0;
                data.Add(machineCode.SliceWithZeroPadding((UInt256)i + 1, immediatesCount).ToArray());
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
    public static void Analyse(CodeInfo codeInfo, IlevmMode mode, IVMConfig vmConfig, ILogger logger)
    {
        Metrics.IlvmContractsAnalyzed++;
        ReadOnlyMemory<byte> machineCode = codeInfo.MachineCode;

        static void SegmentCode(CodeInfo codeInfo, (OpcodeInfo[], byte[][]) codeData, IlInfo ilinfo, IVMConfig vmConfig)
        {
            List<InstructionChunk> segmentsFound = new();
            int offset = ilinfo.IlevmChunks?.Length ?? 0;   
            if (codeData.Item1.Length == 0)
            {
                return;
            }

            string GenerateName(Range segmentRange) => $"ILEVM_PRECOMPILED_({codeInfo.Address})[{segmentRange.Start}..{segmentRange.End}]";

            int[] statefulOpcodeindex = new int[codeData.Item1.Length];

            int j = 0;
            for (int i = 0; i < codeData.Item1.Length; i++)
            {
                if (codeData.Item1[i].Operation.IsStateful())
                {
                    statefulOpcodeindex[j++] = i;
                }
            }

            long segmentAvgSize = 0;
            for (int i = -1; i < j; i++)
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

                segmentAvgSize += segment.Length;

                if(segment.Length <= 1)
                {
                    continue;
                }

                var segmentExecutionCtx = CompileSegment(segmentName, codeInfo, segment, codeData.Item2, vmConfig, out int[] JumpDestinations);
                ilinfo.AddMapping(segment[0].ProgramCounter, segmentsFound.Count + offset, ILMode.PARTIAL_AOT_MODE);
                if (vmConfig.AggressivePartialAotMode)
                {
                    for (int k = 0; k < JumpDestinations.Length; k++)
                    {
                        ilinfo.AddMapping(JumpDestinations[k], segmentsFound.Count + offset, ILMode.PARTIAL_AOT_MODE);
                    }
                }
                segmentsFound.Add(segmentExecutionCtx);
            }

            Interlocked.Or(ref ilinfo.Mode, ILMode.PARTIAL_AOT_MODE);
            if(segmentsFound.Count == 0)
            {
                return;
            }

            if (ilinfo.IlevmChunks is null)
            {
                ilinfo.IlevmChunks = segmentsFound.ToArray();
            }
            else
            {
                List<InstructionChunk> combined = [
                    ..ilinfo.IlevmChunks,
                    ..segmentsFound
                ];
                ilinfo.IlevmChunks = combined.ToArray();
            }
        }

        static void CheckPatterns(ReadOnlyMemory<byte> machineCode, IlInfo ilinfo)
        {
            List<InstructionChunk> patternsFound = new();
            int offset = ilinfo.IlevmChunks?.Length ?? 0;   
            var (strippedBytecode, data) = StripByteCode(machineCode.Span);
            foreach ((Type _, IPatternChunk chunkHandler) in _patterns)
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
                        ilinfo.AddMapping(strippedBytecode[i].ProgramCounter, patternsFound.Count + offset, ILMode.PATTERN_BASED_MODE);
                        patternsFound.Add(chunkHandler);
                        i += chunkHandler.Pattern.Length - 1;
                    }
                }
            }

            Interlocked.Or(ref ilinfo.Mode, ILMode.PATTERN_BASED_MODE);
            if (patternsFound.Count == 0)
            {
                return;
            }

            if (ilinfo.IlevmChunks is null) {
                ilinfo.IlevmChunks = patternsFound.ToArray();
            }
            else
            {
                List<InstructionChunk> combined = [
                    ..ilinfo.IlevmChunks,
                    ..patternsFound
                ];

                ilinfo.IlevmChunks = combined.ToArray();
            }
        }

        switch (mode)
        {
            case ILMode.PATTERN_BASED_MODE:
                CheckPatterns(machineCode, codeInfo.IlInfo);
                break;
            case ILMode.PARTIAL_AOT_MODE:
                SegmentCode(codeInfo, StripByteCode(machineCode.Span), codeInfo.IlInfo, vmConfig);
                break;
        }
    }
}
