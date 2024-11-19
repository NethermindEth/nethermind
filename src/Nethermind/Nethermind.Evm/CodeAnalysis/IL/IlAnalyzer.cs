// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Config;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using static Nethermind.Evm.CodeAnalysis.IL.ILCompiler;
using static Nethermind.Evm.CodeAnalysis.IL.IlInfo;
using static Org.BouncyCastle.Crypto.Engines.SM2Engine;
using ILMode = int;

[assembly : InternalsVisibleTo("Nethermind.Evm.Tests")]
[assembly : InternalsVisibleTo("Nethermind.Evm.Benchmarks")]
namespace Nethermind.Evm.CodeAnalysis.IL;

/// <summary>
/// Provides
/// </summary>
public static class IlAnalyzer
{
    public class AnalysisWork(CodeInfo codeInfo, ILMode mode)
    {
        public CodeInfo CodeInfo = codeInfo;
        public ILMode Mode = mode;
    }
    private static readonly ConcurrentQueue<AnalysisWork> _queue = new();

    public static void Enqueue(CodeInfo codeInfo, ILMode mode, IVMConfig config, ILogger logger)
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
            if (logger.IsInfo) logger.Info($"Starting IL-EVM analysis of code {worklet.CodeInfo.Address}");
            IlAnalyzer.Analyse(worklet.CodeInfo, worklet.Mode, config, logger);
        }
    }

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
    public static void Analyse(CodeInfo codeInfo, ILMode mode, IVMConfig vmConfig, ILogger logger)
    {
        Metrics.IlvmContractsAnalyzed++;
        ReadOnlyMemory<byte> machineCode = codeInfo.MachineCode;

        static void SegmentCode(CodeInfo codeInfo, (OpcodeInfo[], byte[][]) codeData, IlInfo ilinfo, IVMConfig vmConfig)
        {
            Dictionary<int, PrecompiledChunk> segmentMap = new();

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

                var segmentExecutionCtx = CompileSegment(segmentName, codeInfo, segment, codeData.Item2, vmConfig);
                if (vmConfig.AggressiveJitMode)
                {
                    segmentMap.Add(segment[0].ProgramCounter, segmentExecutionCtx);
                    for (int k = 0; k < segmentExecutionCtx.JumpDestinations.Length; k++)
                    {
                        segmentMap.TryAdd(segmentExecutionCtx.JumpDestinations[k], segmentExecutionCtx);
                    }
                }
                else
                {
                    segmentMap.Add(segment[0].ProgramCounter, segmentExecutionCtx);
                }
            }

            ilinfo.Segments = segmentMap.ToFrozenDictionary();
            Interlocked.Or(ref ilinfo.Mode, IlInfo.ILMode.JIT_MODE);
        }

        static void CheckPatterns(ReadOnlyMemory<byte> machineCode, IlInfo ilinfo)
        {
            Dictionary<int, InstructionChunk> chunkMap = new();
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
                        chunkMap.Add((ushort)strippedBytecode[i].ProgramCounter, chunkHandler);
                        i += chunkHandler.Pattern.Length - 1;
                    }
                }
            }

            ilinfo.Chunks = chunkMap.ToFrozenDictionary();
            Interlocked.Or(ref ilinfo.Mode, IlInfo.ILMode.PAT_MODE);
        }

        switch (mode)
        {
            case IlInfo.ILMode.PAT_MODE:
                if (logger.IsInfo) logger.Info($"Analyzing patterns of code {codeInfo.Address}");
                CheckPatterns(machineCode, codeInfo.IlInfo);
                break;
            case IlInfo.ILMode.JIT_MODE:
                if (logger.IsInfo) logger.Info($"Precompiling of segments of code {codeInfo.Address}");
                SegmentCode(codeInfo, StripByteCode(machineCode.Span), codeInfo.IlInfo, vmConfig);
                break;
        }
    }
}
