// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
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
    private static int tasksRunningCount = 0;
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
            if(tasksRunningCount < config.IlEvmAnalysisQueueMaxSize)
            {
                Task.Run(() => {
                    Interlocked.Increment(ref tasksRunningCount);
                    AnalyzeQueue(config, logger);
                    Interlocked.Decrement(ref tasksRunningCount);
                });
            }
        }
    }

    private static void AnalyzeQueue(IVMConfig config, ILogger logger)
    {
        int itemsLeft = _queue.Count;
        while (itemsLeft-- > 0 && _queue.TryDequeue(out CodeInfo worklet))
        {
            worklet.IlInfo.AnalysisPhase = AnalysisPhase.Processing;
            try
            {
                Analyse(worklet, config.IlEvmEnabledMode, config, logger);
                worklet.IlInfo.AnalysisPhase = AnalysisPhase.Completed;
            } catch
            {
                worklet.IlInfo.AnalysisPhase = AnalysisPhase.Failed;
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
        Type[] InstructionChunks = typeof(IPatternChunk).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(IPatternChunk))).ToArray();
        foreach (var chunkType in InstructionChunks)
        {
            _patterns[chunkType] = (IPatternChunk)Activator.CreateInstance(chunkType);
        }
    }

    public static IEnumerable<(int, Instruction,  OpcodeMetadata)> EnumerateOpcodes(byte[] machineCode, Range? slice = null)
    {
        slice ??= 0..machineCode.Length;
        OpcodeMetadata metadata = default;
        for (int i = slice.Value.Start.Value; i < slice.Value.End.Value; i += 1 + metadata.AdditionalBytes)
        {
            Instruction opcode = (Instruction)machineCode[i];
            metadata = OpcodeMetadata.GetMetadata(opcode);
            yield return (i, opcode, metadata);
        }
    }

    /// <summary>
    /// For now, return null always to default to EVM.
    /// </summary>
    public static void Analyse(CodeInfo codeInfo, IlevmMode mode, IVMConfig vmConfig, ILogger logger)
    {
        Metrics.IlvmContractsAnalyzed++;
        switch (mode)
        {
            case ILMode.PATTERN_BASED_MODE:
                CheckPatterns(codeInfo.MachineCode.ToArray(), codeInfo.IlInfo);
                break;
            case ILMode.FULL_AOT_MODE:
                if (!AnalyseContract(codeInfo, vmConfig, out ContractCompilerMetadata? compilerMetadata))
                {
                    return;
                }
                CompileContract(codeInfo, compilerMetadata.Value, vmConfig);
                break;
        }
    }

    internal static void CheckPatterns(byte[] bytecode, IlInfo ilinfo)
    {
        ilinfo.IlevmChunks = new InstructionChunk[bytecode.Length];
        var strippedBytecode = EnumerateOpcodes(bytecode).ToArray();

        foreach ((Type _, IPatternChunk chunkHandler) in _patterns)
        {
            for (int i = 0; i < strippedBytecode.Length - chunkHandler.Pattern.Length + 1; i++)
            {
                bool found = true;
                for (int j = 0; j < chunkHandler.Pattern.Length && found; j++)
                {
                    found = ((byte)strippedBytecode[i + j].Item2 == chunkHandler.Pattern[j]);
                }

                if (found)
                {
                    ilinfo.AddMapping(strippedBytecode[i].Item1, chunkHandler);
                    i += chunkHandler.Pattern.Length - 1;
                }
            }
        }
    }

    internal static bool AnalyseContract(CodeInfo codeInfo,  IVMConfig config, out ContractCompilerMetadata? compilerMetadata)
    {
        byte[] codeAsSpan = codeInfo.MachineCode.ToArray();
        Dictionary<int, short> stackOffsets = [];
        Dictionary<int, long> gasOffsets = [];
        Dictionary<int, SubSegmentMetadata> subSegmentData = [];
        Dictionary<int, int> entryPoints = [];

        int startSegment = 0;
        foreach(var (pc, opcode, metadata) in EnumerateOpcodes(codeAsSpan))
        {
            if (opcode is Instruction.JUMPDEST)
            {
                continue;
            }

            if (opcode.IsCall() || opcode.IsCreate())
            {
                int endSegment = pc + metadata.AdditionalBytes + 1;
                entryPoints.Add(startSegment, endSegment);
                AnalyzeSegment(codeAsSpan, startSegment..endSegment, stackOffsets, gasOffsets, subSegmentData);
                startSegment = endSegment;
                continue;
            }
        }

        if (startSegment < codeAsSpan.Length)
        {
            entryPoints.Add(startSegment, codeAsSpan.Length);
            AnalyzeSegment(codeAsSpan, startSegment..codeAsSpan.Length, stackOffsets, gasOffsets, subSegmentData);
        }

        compilerMetadata =  new ContractCompilerMetadata
        {
            StackOffsets = stackOffsets,
            StaticGasSubSegmentes = gasOffsets,
            SubSegments = subSegmentData,
            SegmentsBoundaries = entryPoints
        };
        return true;
    }

    internal static void CompileContract(CodeInfo codeInfo, ContractCompilerMetadata contractMetadata, IVMConfig vmConfig)
    {
        var contractDelegate = Precompiler.CompileContract(codeInfo.Address?.ToString(), codeInfo, contractMetadata, vmConfig);

        codeInfo.IlInfo.PrecompiledContract = contractDelegate;
    }

    internal static void AnalyzeSegment(byte[] fullcode, Range segmentRange, Dictionary<int, short> stackOffsets, Dictionary<int, long> gasOffsets, Dictionary<int, SubSegmentMetadata> subSegmentData)
    {
        SubSegmentMetadata subSegment = new();
        int subsegmentStart = segmentRange.Start.Value;
        int costStart = subsegmentStart;

        subSegment.Start = subsegmentStart;
        short currentStackSize = 0;

        bool hasJumpdest = true;
        bool hasInvalidOpcode = false;

        long coststack = 0;

        bool notStart = true;
        bool lastOpcodeIsAjumpdest = false;

        bool requiresAvailabilityCheck = false;
        bool requiresStaticEnvCheck = false;

        HashSet<Instruction> instructionsIncluded = [];

        foreach (var (pc, op, opcodeMetadata) in EnumerateOpcodes(fullcode, segmentRange))
        {
            lastOpcodeIsAjumpdest = op is Instruction.JUMPDEST;
            stackOffsets[pc] = currentStackSize;
            subSegment.End = pc;
            switch (op)
            {
                case Instruction.JUMPDEST when !notStart:
                    subSegment.Start = subsegmentStart;
                    subSegment.RequiredStack = -subSegment.RequiredStack;
                    subSegment.End = pc - 1;
                    subSegment.IsFailing = hasInvalidOpcode;
                    subSegment.IsReachable = hasJumpdest;
                    subSegment.Instructions = instructionsIncluded;
                    subSegment.RequiresOpcodeCheck = requiresAvailabilityCheck;
                    subSegment.RequiresStaticEnvCheck = requiresStaticEnvCheck;

                    gasOffsets[costStart] = coststack;
                    subSegmentData[subSegment.Start] = subSegment; // remember the stackHeadRef chain of opcodes
                    
                    subsegmentStart = pc;
                    subSegment = new();
                    instructionsIncluded = [op];
                    subSegment.Start = subsegmentStart;

                    costStart = pc;
                    coststack = opcodeMetadata.GasCost;
                    currentStackSize = 0;
                    hasJumpdest = true;
                    hasInvalidOpcode = false;
                    requiresAvailabilityCheck = false;
                    requiresStaticEnvCheck = false;
                    break;
                default:
                    instructionsIncluded.Add(op);
                    coststack += opcodeMetadata.GasCost;
                    subSegment.End = pc;
                    hasInvalidOpcode |= op.IsInvalid();
                    hasJumpdest |= op is Instruction.JUMPDEST;
                    requiresAvailabilityCheck |= op.RequiresAvailabilityCheck();
                    requiresStaticEnvCheck |= opcodeMetadata.IsNotStaticOpcode;
                    // handle stack analysis 
                    currentStackSize -= opcodeMetadata.StackBehaviorPop;
                    if (currentStackSize < subSegment.RequiredStack)
                    {
                        subSegment.RequiredStack = currentStackSize;
                    }

                    currentStackSize += opcodeMetadata.StackBehaviorPush;
                    if (currentStackSize > subSegment.MaxStack)
                    {
                        subSegment.MaxStack = currentStackSize;
                    }

                    subSegment.LeftOutStack = currentStackSize;
                    if (op.IsTerminating() || op.IsJump() || op.IsCall() || op.IsCreate() || op is Instruction.GAS)
                    {
                        if (op is not Instruction.GAS)
                        {
                            subSegment.Start = subsegmentStart;
                            subSegment.RequiredStack = -subSegment.RequiredStack;
                            subSegment.IsFailing = hasInvalidOpcode;
                            subSegment.IsReachable = hasJumpdest;
                            subSegment.Instructions = instructionsIncluded;
                            subSegment.RequiresOpcodeCheck = requiresAvailabilityCheck;
                            subSegment.RequiresStaticEnvCheck = requiresStaticEnvCheck;

                            gasOffsets[costStart] = coststack;
                            subSegmentData[subSegment.Start] = subSegment; // remember the stackHeadRef chain of opcodes

                            subsegmentStart = pc + 1;
                            subSegment = new();

                            instructionsIncluded = [];
                            currentStackSize = 0;
                            hasJumpdest = op is Instruction.JUMPI;
                            hasInvalidOpcode = false;
                            costStart = pc + 1;             // start with the next again
                            coststack = 0;
                            requiresAvailabilityCheck = false;
                            requiresStaticEnvCheck = false;

                            notStart = true;
                            continue;
                        }
                        else
                        {
                            gasOffsets[costStart] = coststack;
                            costStart = pc + 1;             // start with the next again
                            coststack = 0;
                        }
                    }
                    break;
            }
            notStart = false;
        }

        if ((subsegmentStart < segmentRange.End.Value && !subSegmentData.ContainsKey(subsegmentStart)) || lastOpcodeIsAjumpdest)
        {
            subSegment.Start = subsegmentStart;
            subSegment.IsReachable = hasJumpdest;
            subSegment.IsFailing = hasInvalidOpcode;
            subSegment.RequiredStack = -subSegment.RequiredStack;
            subSegment.End = segmentRange.End.Value-1;
            subSegment.Instructions = instructionsIncluded;
            subSegment.RequiresOpcodeCheck = requiresAvailabilityCheck;
            subSegment.RequiresStaticEnvCheck = requiresStaticEnvCheck;

            gasOffsets[costStart] = coststack;
            subSegmentData[subSegment.Start] = subSegment;
        }
    }
}
