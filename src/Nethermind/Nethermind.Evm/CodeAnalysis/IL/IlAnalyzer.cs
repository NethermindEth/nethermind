// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis.IL.Delegates;
using Nethermind.Evm.Config;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
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
    private static Channel<CodeInfo> _channel = Channel.CreateUnbounded<CodeInfo>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = true,
    });

    private static Task? _workerTask;
    private static CancellationTokenSource _cts = new();

    public static void Enqueue(CodeInfo codeInfo, IVMConfig config, ILogger logger)
    {
        if (Interlocked.CompareExchange(ref codeInfo.IlInfo.AnalysisPhase, AnalysisPhase.Queued, AnalysisPhase.NotStarted) != AnalysisPhase.NotStarted)
        {
            return;
        }

        Metrics.IncrementIlvmAotQueueSize();
        _channel.Writer.TryWrite(codeInfo);
    }
    public static void StartPrecompilerBackgroundThread(IVMConfig config, ILogger logger)
    {
        if (_workerTask is not null && !_workerTask.IsCompleted)
        {
            return;
        }

        _workerTask = Task.Factory.StartNew(async () => await WorkerLoop(Math.Max(1, (int)(config.IlEvmAnalysisCoreUsage * Environment.ProcessorCount)), config, logger), _cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    }

    public static async Task StopPrecompilerBackgroundThread(IVMConfig vMConfig)
    {
        _channel.Writer.Complete(); // signal end of data
        _cts.Cancel();              // in case of forced shutdown

        if (_workerTask is not null)
            await _workerTask.ConfigureAwait(false);

        Precompiler.FlushToDisk(vMConfig!);
    }

    private static async Task WorkerLoop(int taskLimit, IVMConfig config, ILogger logger)
    {
        try
        {
            Task[] taskPool = new Task[taskLimit];
            Array.Fill(taskPool, Task.CompletedTask);

            await foreach (var codeInfo in _channel.Reader.ReadAllAsync(_cts.Token))
            {
                int index = Task.WaitAny(taskPool);

                Metrics.DecrementIlvmAotQueueSize();
                taskPool[index] = Task.Run(() => ProcessCodeInfoAsync(config, logger, codeInfo));
            }
        }
        catch (Exception ex)
        {
            logger.Error("Unhandled exception in ILVM background worker: " + ex);
        }
    }

    private static Task ProcessCodeInfoAsync(IVMConfig config, ILogger logger, CodeInfo worklet)
    {
        worklet.IlInfo.AnalysisPhase = AnalysisPhase.Processing;
        try
        {
            Analyse(worklet, config.IlEvmEnabledMode, config, logger);
        }
        catch (Exception e)
        {
            logger.Error($"IlAnalyzer: {worklet.Codehash} failed to analyze error : {e.Message}");
            Interlocked.Exchange(ref worklet.IlInfo.AnalysisPhase, AnalysisPhase.Failed);
            throw;
        }
        finally
        {
            Metrics.IncrementIlvmAotContractsProcessed();
        }

        return Task.CompletedTask;
    }

    public static IEnumerable<(int, Instruction, OpcodeMetadata)> EnumerateOpcodes(byte[] machineCode, Range? slice = null)
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
    public static void Analyse(CodeInfo codeInfo, ILMode mode, IVMConfig vmConfig, ILogger logger)
    {
        switch (mode)
        {
            case ILMode.DYNAMIC_AOT_MODE:
                if (AotContractsRepository.TryGetIledCode(codeInfo.Codehash.Value, out ILEmittedEntryPoint? contractDelegate))
                {
                    codeInfo.IlInfo.PrecompiledContract = contractDelegate;
                    Metrics.IncrementIlvmAotCacheTouched();
                    break;
                }
                else
                {
                    if (!AnalyseContract(codeInfo, vmConfig, out ContractCompilerMetadata? compilerMetadata))
                    {
                        Interlocked.Exchange(ref codeInfo.IlInfo.AnalysisPhase, AnalysisPhase.Skipped);
                        return;
                    }

                    if (!TryCompileContract(codeInfo, compilerMetadata.Value, vmConfig))
                    {
                        Interlocked.Exchange(ref codeInfo.IlInfo.AnalysisPhase, AnalysisPhase.Failed);
                        return;
                    }
                    Metrics.IncrementIlvmContractsAnalyzed();
                }
                break;
        }
        Interlocked.Exchange(ref codeInfo.IlInfo.AnalysisPhase, AnalysisPhase.Completed);
    }

    internal static bool TryCompileContract(CodeInfo codeInfo, ContractCompilerMetadata contractMetadata, IVMConfig vmConfig)
    {
        Metrics.IncrementIlvmCurrentlyCompiling();
        if (Precompiler.TryCompileContract(codeInfo.Codehash?.ToString(), codeInfo, contractMetadata, vmConfig, SimpleConsoleLogManager.Instance.GetLogger("IlvmLogger"), out ILEmittedEntryPoint? contractDelegate))
        {
            AotContractsRepository.AddIledCode(codeInfo.Codehash.Value, contractDelegate);
            codeInfo.IlInfo.PrecompiledContract = contractDelegate;
            return true;
        }
        Metrics.DecrementIlvmCurrentlyCompiling();
        return false;
    }

    internal static bool AnalyseContract(CodeInfo codeInfo, IVMConfig config, out ContractCompilerMetadata? compilerMetadata)
    {
        Metrics.IncrementIlvmCurrentlyAnalysing();

        byte[] codeAsSpan = codeInfo.MachineCode.ToArray();
        Dictionary<int, short> stackOffsets = [];
        Dictionary<int, long> gasOffsets = [];
        Dictionary<int, SubSegmentMetadata> subSegmentData = [];

        AnalyzeSegment(codeAsSpan, 0..codeAsSpan.Length, stackOffsets, gasOffsets, subSegmentData);

        compilerMetadata = new ContractCompilerMetadata
        {
            StackOffsets = stackOffsets,
            StaticGasSubSegmentes = gasOffsets,
            SubSegments = subSegmentData,
        };

        Metrics.DecrementIlvmCurrentlyAnalysing();
        return true;
    }

    internal static void AnalyzeSegment(byte[] fullcode, Range segmentRange, Dictionary<int, short> stackOffsets, Dictionary<int, long> gasOffsets, Dictionary<int, SubSegmentMetadata> subSegmentData)
    {
        SubSegmentMetadata subSegment = new();
        int subsegmentStart = segmentRange.Start.Value;
        int costStart = subsegmentStart;

        subSegment.IsEntryPoint = true; // the first segment is always an entry point
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
                    if(op is Instruction.GAS)
                    {
                        gasOffsets[costStart] = coststack;
                        costStart = pc + 1;             // start with the next again
                        coststack = 0;
                    }
                    else if(op.IsJump() || op.IsTerminating())
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
                    } else if(op.IsCreate() || op.IsCall())
                    {
                        // create will be trated like a JUMPI but with a special gas handling like GAS

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

                        subSegment.IsEntryPoint = true; // create is not an entry point
                        instructionsIncluded = [];
                        currentStackSize = 0;
                        hasJumpdest = true;
                        hasInvalidOpcode = false;
                        costStart = pc + 1;             // start with the next again
                        coststack = 0;
                        requiresAvailabilityCheck = false;
                        requiresStaticEnvCheck = false;

                        notStart = true;
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
            subSegment.End = segmentRange.End.Value - 1;
            subSegment.Instructions = instructionsIncluded;
            subSegment.RequiresOpcodeCheck = requiresAvailabilityCheck;
            subSegment.RequiresStaticEnvCheck = requiresStaticEnvCheck;

            gasOffsets[costStart] = coststack;
            subSegmentData[subSegment.Start] = subSegment;
        }
    }
}
