using System;
using System.Runtime.CompilerServices;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Logging;
using Nethermind.State;
using static Nethermind.Evm.VirtualMachine;

[assembly: InternalsVisibleTo("Nethermind.Evm.Tests")]
[assembly: InternalsVisibleTo("Nethermind.Evm.Benchmarks")]

namespace Nethermind.Evm.CodeAnalysis.IL;

public static class ILMode
{
    public const int NO_ILVM            = 0b00000000;
    public const int PATTERN_BASED_MODE = 0b00000001;
    public const int FULL_AOT_MODE      = 0b00000010;
}

public enum AnalysisPhase
{
    NotStarted, Queued, Processing, Completed
}

/// <summary>
/// Represents the IL-EVM information about the contract.
/// </summary>
/// 
internal class IlInfo
{

    /// <summary>
    /// Represents an information about IL-EVM being not able to optimize the given <see cref="CodeInfo"/>.
    /// </summary>
    public static IlInfo Empty(int size) => new(size);

    public bool IsNotProcessed => AnalysisPhase is AnalysisPhase.NotStarted;


    public AnalysisPhase AnalysisPhase = AnalysisPhase.NotStarted;
    /// <summary>
    /// No overrides.
    /// </summary>
    private IlInfo(int bytecodeSize)
    {
        IlevmChunks = new InstructionChunk[bytecodeSize];
    }

    // assumes small number of ILed
    public InstructionChunk[] IlevmChunks { get; set; }

    public ContractMetadata? ContractMetadata { get; set; }
    public PrecompiledContract? PrecompiledContract { get; set; }

    public void AddMapping(int index, InstructionChunk handler)
    {
        // in this code ILMODE is used to mark pc if it points to compiled or pattern chunk
        // NO_ILVM is used to mark pc if it points to non-compiled chunk
        IlevmChunks[index] = handler;
    }

    public bool TryExecute<TTracingInstructions>(
        ILogger logger,
        EvmState vmState,
        in ExecutionEnvironment env,
        in TxExecutionContext txCtx,
        in BlockExecutionContext blkCtx,
        ulong chainId,
        IWorldState worldState,
        IBlockhashProvider blockHashProvider,
        ICodeInfoRepository codeinfoRepository,
        IReleaseSpec spec,
        ITxTracer tracer,
        ref int programCounter,
        ref long gasAvailable,
        ref EvmStack<TTracingInstructions> stack,
        ref ReadOnlyMemory<byte> returnDataBuffer,
        ref ILChunkExecutionState result)

        where TTracingInstructions : struct, VirtualMachine.IIsTracing
    {
        if (programCounter > ushort.MaxValue || this.IsNotProcessed)
            return false;

        var bytecodeChunkHandler = IlevmChunks[programCounter];
        if (bytecodeChunkHandler is not null)
        {
            Metrics.IlvmPredefinedPatternsExecutions++;
            if (typeof(TTracingInstructions) == typeof(IsTracing))
                StartTracingSegment(in vmState, in stack, tracer, programCounter, gasAvailable, bytecodeChunkHandler);

            bytecodeChunkHandler.Invoke(vmState, chainId, in env, in txCtx, in blkCtx, blockHashProvider, worldState, codeinfoRepository, spec, ref programCounter, ref gasAvailable, ref stack, ref returnDataBuffer, tracer, logger, ref result);
            if (typeof(TTracingInstructions) == typeof(IsTracing))
                tracer.ReportOperationRemainingGas(gasAvailable);
            return true;
        }
        return false;
    }

    private static void StartTracingSegment<T, TTracingInstructions>(in EvmState vmState, in EvmStack<TTracingInstructions> stack, ITxTracer tracer, int programCounter, long gasAvailable, T chunk)
        where TTracingInstructions : struct, VirtualMachine.IIsTracing
        where T : InstructionChunk
    {
        tracer.ReportIlEvmChunkExecution(gasAvailable, programCounter, chunk.Name, vmState.Env);

        if (tracer.IsTracingMemory)
        {
            tracer.SetOperationMemory(vmState.Memory.GetTrace());
            tracer.SetOperationMemorySize(vmState.Memory.Size);
        }

        if (tracer.IsTracingStack)
        {
            Memory<byte> stackMemory = vmState.DataStack.AsMemory().Slice(0, stack.Head * EvmStack<VirtualMachine.IsTracing>.WordSize);
            tracer.SetOperationStack(new TraceStack(stackMemory));
        }
    }
}
