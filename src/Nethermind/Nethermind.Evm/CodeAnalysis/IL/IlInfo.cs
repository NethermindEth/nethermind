using System;
using System.Runtime.CompilerServices;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.State;
using static Nethermind.Evm.VirtualMachine;

[assembly: InternalsVisibleTo("Nethermind.Evm.Tests")]
[assembly: InternalsVisibleTo("Nethermind.Evm.Benchmarks")]

namespace Nethermind.Evm.CodeAnalysis.IL;
internal ref struct ILChunkExecutionResult(ref ReadOnlyMemory<byte> output)
{
    public readonly bool ShouldFail => ExceptionType != EvmExceptionType.None;
    public bool ShouldJump;
    public bool ShouldStop;
    public bool ShouldRevert;
    public bool ShouldReturn;
    public ref readonly ReadOnlyMemory<byte> ReturnData = ref output;
    public EvmExceptionType ExceptionType;
}

public static class ILMode
{
    public const int NO_ILVM = 0b00000000;
    public const int PATTERN_BASED_MODE = 0b10000000;
    public const int PARTIAL_AOT_MODE = 0b01000000;
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

    /// <summary>
    /// Represents what mode of IL-EVM is used. 0 is the default. [0 = No ILVM optimizations, 1 = Pattern matching, 2 = subsegments compiling]
    /// </summary>
    public int Mode = ILMode.NO_ILVM;
    public bool IsEmpty => IlevmChunks is null && Mode == ILMode.NO_ILVM;
    public bool IsBeingProcessed = false;
    /// <summary>
    /// No overrides.
    /// </summary>
    private IlInfo(int bytecodeSize)
    {
        IlevmChunks = default;
        _Mapping = new byte[bytecodeSize];
    }

    // assumes small number of ILed
    public InstructionChunk[]? IlevmChunks { get; set; }

    private byte[] _Mapping = null;

    public void AddMapping(int index, int chunkIdx, int mode)
    {
        // in this code ILMODE is used to mark pc if it points to compiled or pattern chunk
        // NO_ILVM is used to mark pc if it points to non-compiled chunk
        _Mapping[index] = (byte)(chunkIdx | mode);
    }

    public bool TryExecute<TTracingInstructions>(
        ILogger logger,
        EvmState vmState,
        in ExecutionEnvironment env,
        in TxExecutionContext txCtx,
        in BlockExecutionContext blkCtx,
        ulong chainId,
        ref ReadOnlyMemory<byte> outputBuffer,
        IWorldState worldState,
        IBlockhashProvider blockHashProvider,
        ICodeInfoRepository codeinfoRepository,
        IReleaseSpec spec,
        ITxTracer tracer,
        ref int programCounter,
        ref long gasAvailable,
        ref EvmStack<TTracingInstructions> stack,
        ref ILChunkExecutionResult result)

        where TTracingInstructions : struct, VirtualMachine.IIsTracing
    {
        if (programCounter > ushort.MaxValue || this.IsEmpty)
            return false;

        if (Mode != ILMode.NO_ILVM && _Mapping[programCounter] != ILMode.NO_ILVM)
        {
            var bytecodeChunkHandler = IlevmChunks[_Mapping[programCounter] & 0x3F];
            Metrics.IlvmPrecompiledSegmentsExecutions++;
            if (typeof(TTracingInstructions) == typeof(IsTracing))
                StartTracingSegment(in vmState, in stack, tracer, programCounter, gasAvailable, bytecodeChunkHandler);

            bytecodeChunkHandler.Invoke(vmState, chainId, ref outputBuffer, in env, in txCtx, in blkCtx, blockHashProvider, worldState, codeinfoRepository, spec, ref programCounter, ref gasAvailable, ref stack, tracer, ref result);
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
