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
using System.Threading;
using System.Threading.Tasks;
using static Nethermind.Evm.CodeAnalysis.IL.ILCompiler;
using static Nethermind.Evm.CodeAnalysis.IL.IlInfo;
using static Org.BouncyCastle.Crypto.Engines.SM2Engine;
using ILMode = int;

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
        if(config.AnalysisQueueMaxSize == _queue.Count)
        {
            Task.Run(() => AnalyzeQueue(config, logger));
        }
    }

    private static void AnalyzeQueue(IVMConfig config, ILogger logger)
    {
        int itemsLeft = config.AnalysisQueueMaxSize;
        while (itemsLeft-- > 0 && _queue.TryDequeue(out AnalysisWork worklet))
        {
            if (logger.IsInfo) logger.Info($"Starting IL-EVM analysis of code {worklet.CodeInfo.Address}");
            IlAnalyzer.StartAnalysis(worklet.CodeInfo, worklet.Mode, config, logger);
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
        for (ushort i = 0; i < machineCode.Length; i++, j++)
        {
            Instruction opcode = (Instruction)machineCode[i];
            int? argsIndex = null;
            ushort pc = i;
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
    internal static void StartAnalysis(CodeInfo codeInfo, ILMode mode, IVMConfig vmConfig, ILogger logger)
    {
        Metrics.IlvmContractsAnalyzed++;
        ReadOnlyMemory<byte> machineCode = codeInfo.MachineCode;

        static void SegmentCode(CodeInfo codeInfo, (OpcodeInfo[], byte[][]) codeData, IlInfo ilinfo, IVMConfig vmConfig)
        {
            if (codeData.Item1.Length == 0)
            {
                return;
            }

            string GenerateName(Range segmentRange) => $"ILEVM_PRECOMPILED_({codeInfo.Address})[{segmentRange.Start}..{segmentRange.End}]";
            void HandleSegment(int start, int end)
            {
                if (start >= end)
                {
                    return;
                }

                var segment = codeData.Item1[start..end];
                var firstOp = segment[0];
                var lastOp = segment[^1];
                var segmentName = GenerateName(firstOp.ProgramCounter..(lastOp.ProgramCounter + lastOp.Metadata.AdditionalBytes));

                var segmentExecutionCtx = CompileSegment(segmentName, segment, codeData.Item2, vmConfig);
                ilinfo.Segments.GetOrAdd(segment[0].ProgramCounter, segmentExecutionCtx);
            }

            int nextStart = 0;
            int currentEnd = codeData.Item1.Length; // non inclusive
            for (int i = 0; i < codeData.Item1.Length; i++)
            {
                int currentStart = nextStart; // inclusive
                currentEnd = codeData.Item1.Length; // non inclusive
                if (codeData.Item1[i].Operation.IsStateful())
                {
                    // will start at current start and stop at opcode before the current one
                    currentEnd = i;
                    // next start will be the opcode after the current one
                    nextStart = i + 1;

                    HandleSegment(currentStart, currentEnd);
                }
                else if (codeData.Item1[i].Operation.IsJumpOrJumpdest())
                {
                    if(codeData.Item1[i].Operation is Instruction.JUMPDEST)
                    {
                        // current end will be the opcode before the jumpdest
                        currentEnd = i;
                        // next start will be the jumpdest
                        nextStart = i;
                    } else
                    {
                        // the end will be the jump opcode itself
                        currentEnd = i + 1;
                        // next start will be the opcode after the jump
                        nextStart = i + 1;
                    }

                    HandleSegment(currentStart, currentEnd);
                }
            }

            if(nextStart != currentEnd && !ilinfo.Segments.ContainsKey(codeData.Item1[nextStart].ProgramCounter))
            {
                HandleSegment(nextStart, codeData.Item1.Length);
            }

            Interlocked.Or(ref ilinfo.Mode, IlInfo.ILMode.JIT_MODE);
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
