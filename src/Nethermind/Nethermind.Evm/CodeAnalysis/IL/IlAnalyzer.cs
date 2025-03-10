// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis.IL.CompilerModes;
using Nethermind.Evm.Config;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using IlevmMode = int;

[assembly: InternalsVisibleTo("Nethermind.Evm.Tests")]
[assembly: InternalsVisibleTo("Nethermind.Evm.Benchmarks")]
namespace Nethermind.Evm.CodeAnalysis.IL;

/// <summary>
/// Provides
/// </summary>
public static class IlAnalyzer
{
    private static readonly ConcurrentQueue<CodeInfo> _queue = new();

    public static void Enqueue(CodeInfo codeInfo, IVMConfig config, ILogger logger)
    {
        if(codeInfo.IlInfo.AnalysisPhase is not AnalysisPhase.NotStarted)
        {
            return;
        }

        codeInfo.IlInfo.AnalysisPhase = AnalysisPhase.Queued;
        _queue.Enqueue(codeInfo);

        if (config.IlEvmAnalysisQueueMaxSize <= _queue.Count)
        {
            Task.Run(() => AnalyzeQueue(config, logger));
        }
    }

    private static void AnalyzeQueue(IVMConfig config, ILogger logger)
    {
        int itemsLeft = _queue.Count;
        while (itemsLeft-- > 0 && _queue.TryDequeue(out CodeInfo worklet))
        {
            worklet.IlInfo.AnalysisPhase = AnalysisPhase.Processing;
            Analyse(worklet, config.IlEvmEnabledMode, config, logger);
            worklet.IlInfo.AnalysisPhase = AnalysisPhase.Completed;
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
        Type[] InstructionChunks = typeof(IPatternChunk).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(IPatternChunk))).ToArray();
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

        ContractMetadata metadata;
        if((metadata = AnalyseContract(codeInfo, StripByteCode(machineCode.Span), vmConfig)) is null)
        {
            return;
        }

        codeInfo.IlInfo.ContractMetadata ??= metadata;

        switch (mode)
        {
            case ILMode.PATTERN_BASED_MODE:
                CheckPatterns(codeInfo.IlInfo.ContractMetadata, codeInfo.IlInfo);
                break;
            case ILMode.FULL_AOT_MODE:
                CompileContract(codeInfo, codeInfo.IlInfo.ContractMetadata, codeInfo.IlInfo, vmConfig);
                break;

        }
    }

    internal static void CheckPatterns(ContractMetadata contractMetadata, IlInfo ilinfo)
    {
        var strippedBytecode = contractMetadata.Opcodes;

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
                    ilinfo.AddMapping(strippedBytecode[i].ProgramCounter, chunkHandler);
                    i += chunkHandler.Pattern.Length - 1;
                }
            }
        }
    }

    internal static ContractMetadata? AnalyseContract(CodeInfo codeInfo,  (OpcodeInfo[], byte[][]) codeData, IVMConfig config)
    {
        if (codeData.Item1.Length == 0)
        {
            return null;
        }

        List<SegmentMetadata> segments = new();
        List<int> jumpdests = new();


        int startSegment = 0;
        for (int i = 0; i < codeData.Item1.Length; i++)
        {
            if (codeData.Item1[i].Operation is Instruction.JUMPDEST)
            {
                jumpdests.Add(codeData.Item1[i].ProgramCounter);
            }

            if (codeData.Item1[i].IsBlocking)
            {
                int endSegment = i + 1;
                segments.Add(AnalyzeSegment(codeData.Item1, startSegment..endSegment));
                startSegment = i + 1;
                continue;
            }
        }

        if (startSegment < codeData.Item1.Length)
        {
            segments.Add(AnalyzeSegment(codeData.Item1, startSegment..));
        }

        var contractMetadata = new ContractMetadata
        {
            TargetCodeInfo = codeInfo,
            Opcodes = codeData.Item1,
            Segments = segments.ToArray(),
            Jumpdests = jumpdests.ToArray(),
            EmbeddedData = codeData.Item2
        };

        return contractMetadata;
    }

    internal static void CompileContract(CodeInfo codeInfo, ContractMetadata contractMetadata, IlInfo ilinfo, IVMConfig vmConfig)
    {
        var contractDelegate = Precompiler.CompileContract(codeInfo.Address?.ToString(), contractMetadata, vmConfig);

        ilinfo.PrecompiledContract = contractDelegate;
    }

    internal static SegmentMetadata AnalyzeSegment(OpcodeInfo[] fullcode, Range segmentRange)
    {
        SegmentMetadata metadata = new()
        {
            Segment = [.. fullcode[segmentRange]]
        };

        SubSegmentMetadata subSegment = new();
        metadata.SubSegments = new();

        int subsegmentStart = 0;
        int costStart = 0;

        subSegment.SetInitialStackData(metadata.Segment[subsegmentStart].Metadata.StackBehaviorPop, metadata.Segment[subsegmentStart].Metadata.StackBehaviorPush - metadata.Segment[subsegmentStart].Metadata.StackBehaviorPop, metadata.Segment[subsegmentStart].Metadata.StackBehaviorPush);
        subSegment.Start = subsegmentStart;
        metadata.StackOffsets = new int[metadata.Segment.Length];
        int currentStackSize = 0;

        bool hasJumpdest = true;
        bool hasInvalidOpcode = false;

        long coststack = 0;

        bool notStart = true;
        bool lastOpcodeIsAjumpdest = false;

        for (int pc = 0; pc < metadata.Segment.Length; pc++)
        {
            OpcodeInfo op = metadata.Segment[pc];
            lastOpcodeIsAjumpdest = op.Operation is Instruction.JUMPDEST;
            metadata.StackOffsets[pc] = currentStackSize;
            subSegment.End = pc;
            switch (op.Operation)
            {
                case Instruction.JUMPDEST when !notStart:
                    subSegment.Start = subsegmentStart;
                    subSegment.RequiredStack = -subSegment.RequiredStack;
                    subSegment.End = pc - 1;
                    subSegment.IsFailing = hasInvalidOpcode;
                    subSegment.IsReachable = hasJumpdest;
                    subSegment.StaticGasSubSegmentes[costStart] = coststack;

                    if(subSegment.Start <= subSegment.End)
                    {
                        subSegment.SubSegment = metadata.Segment[subSegment.Start..(subSegment.End + 1)];
                    }

                    metadata.SubSegments[subSegment.Start] = subSegment; // remember the stackHeadRef chain of opcodes


                    subsegmentStart = pc;
                    subSegment = new();
                    subSegment.Start = subsegmentStart;

                    costStart = pc;
                    coststack = op.Metadata.GasCost;
                    currentStackSize = 0;
                    hasJumpdest = true;
                    hasInvalidOpcode = false;
                    break;
                default:
                    coststack += op.Metadata.GasCost;
                    subSegment.End = pc;
                    hasInvalidOpcode |= op.IsInvalid;
                    // handle stack analysis 
                    currentStackSize -= op.Metadata.StackBehaviorPop;
                    if (currentStackSize < subSegment.RequiredStack)
                    {
                        subSegment.RequiredStack = currentStackSize;
                    }

                    currentStackSize += op.Metadata.StackBehaviorPush;
                    if (currentStackSize > subSegment.MaxStack)
                    {
                        subSegment.MaxStack = currentStackSize;
                    }

                    subSegment.LeftOutStack = currentStackSize;
                    if (op.IsTerminating || op.IsJump || op.IsBlocking ||op.Operation is Instruction.GAS)
                    {
                        if (op.Operation is not Instruction.GAS)
                        {
                            subSegment.Start = subsegmentStart;
                            subSegment.RequiredStack = -subSegment.RequiredStack;
                            subSegment.IsFailing = hasInvalidOpcode;
                            subSegment.IsReachable = hasJumpdest;

                            subSegment.StaticGasSubSegmentes[costStart] = coststack; // remember the stackHeadRef chain of opcodes
                            subSegment.SubSegment = metadata.Segment[subSegment.Start..(subSegment.End+1)];

                            metadata.SubSegments[subSegment.Start] = subSegment; // remember the stackHeadRef chain of opcodes

                            subsegmentStart = pc + 1;
                            subSegment = new();

                            currentStackSize = 0;
                            hasJumpdest = op.Operation is Instruction.JUMPI;
                            hasInvalidOpcode = false;
                            costStart = pc + 1;             // start with the next again
                            coststack = 0;
                        }
                        else
                        {
                            subSegment.StaticGasSubSegmentes[costStart] = coststack; // remember the stackHeadRef chain of opcodes
                            costStart = pc + 1;             // start with the next again
                            coststack = 0;
                        }
                    }
                    break;
            }
            notStart = false;
        }

        if ((subsegmentStart < metadata.Segment.Length && !metadata.SubSegments.ContainsKey(subsegmentStart)) || lastOpcodeIsAjumpdest)
        {
            subSegment.Start = subsegmentStart;
            subSegment.StaticGasSubSegmentes[costStart] = coststack;
            subSegment.IsReachable = hasJumpdest;
            subSegment.IsFailing = hasInvalidOpcode;
            subSegment.RequiredStack = -subSegment.RequiredStack;
            subSegment.End = metadata.Segment.Length - 1;
            subSegment.SubSegment = metadata.Segment[subSegment.Start..(subSegment.End + 1)];
            metadata.SubSegments[subSegment.Start] = subSegment;
        }

        return metadata;
    }
}
