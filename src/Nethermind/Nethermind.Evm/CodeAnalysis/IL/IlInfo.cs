using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.IdentityModel.Tokens;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.State;
using NonBlocking;
using static Nethermind.Evm.CodeAnalysis.IL.ILCompiler;
using static Nethermind.Evm.VirtualMachine;

[assembly: InternalsVisibleTo("Nethermind.Evm.Tests")]
[assembly: InternalsVisibleTo("Nethermind.Evm.Benchmarks")]

namespace Nethermind.Evm.CodeAnalysis.IL;
/// <summary>
/// Represents the IL-EVM information about the contract.
/// </summary>
internal class IlInfo
{
    internal struct ILChunkExecutionResult
    {
        public readonly bool ShouldFail => ExceptionType != EvmExceptionType.None;
        public bool ShouldJump;
        public bool ShouldStop;
        public bool ShouldRevert;
        public bool ShouldReturn;
        public object ReturnData;
        public EvmExceptionType ExceptionType;


        public static explicit operator ILChunkExecutionResult(ILEvmState state)
        {
            return new ILChunkExecutionResult
            {
                ShouldJump = state.ShouldJump,
                ShouldStop = state.ShouldStop,
                ShouldRevert = state.ShouldRevert,
                ShouldReturn = state.ShouldReturn,
                ReturnData = state.ReturnBuffer,
                ExceptionType = state.EvmException
            };
        }
    }

    public static class ILMode
    {
        public const int NO_ILVM = 0;
        public const int PAT_MODE = 1;
        public const int JIT_MODE = 2;
    }

    /// <summary>
    /// Represents an information about IL-EVM being not able to optimize the given <see cref="CodeInfo"/>.
    /// </summary>
    public static IlInfo Empty => new();

    /// <summary>
    /// Represents what mode of IL-EVM is used. 0 is the default. [0 = No ILVM optimizations, 1 = Pattern matching, 2 = subsegments compiling]
    /// </summary>
    public int Mode = ILMode.NO_ILVM;
    public bool IsEmpty => Chunks is null && Segments is null && Mode == ILMode.NO_ILVM;
    /// <summary>
    /// No overrides.
    /// </summary>
    private IlInfo()
    {
        Chunks = default;
        Segments = default;
    }

    public IlInfo(FrozenDictionary<int, InstructionChunk> mappedOpcodes, FrozenDictionary<int, PrecompiledChunk> segments)
    {
        Chunks = mappedOpcodes;
        Segments = segments;
    }

    // assumes small number of ILed
    public FrozenDictionary<int, InstructionChunk>? Chunks { get; set; }
    public FrozenDictionary<int, PrecompiledChunk>? Segments { get; set; }

    public bool TryExecute<TTracingInstructions>(ILogger logger, EvmState vmState, ulong chainId, ref ReadOnlyMemory<byte> outputBuffer, IWorldState worldState, IBlockhashProvider blockHashProvider, ICodeInfoRepository codeinfoRepository, IReleaseSpec spec, ITxTracer tracer, ref int programCounter, ref long gasAvailable, ref EvmStack<TTracingInstructions> stack, out ILChunkExecutionResult? result)
        where TTracingInstructions : struct, VirtualMachine.IIsTracing
    {
        result = null;
        if (programCounter > ushort.MaxValue || this.IsEmpty)
            return false;

        if (Mode.HasFlag(ILMode.JIT_MODE) && Segments.TryGetValue(programCounter, out PrecompiledChunk aotChunk))
        {
            Metrics.IlvmPrecompiledSegmentsExecutions++;
            if (typeof(TTracingInstructions) == typeof(IsTracing))
                StartTracingSegment(in vmState, in stack, tracer, programCounter, gasAvailable, aotChunk);

            vmState.DataStackHead = stack.Head;
            var ilvmState = new ILEvmState(chainId, vmState, EvmExceptionType.None, programCounter, gasAvailable, ref outputBuffer);

            aotChunk.Invoke(ref ilvmState, blockHashProvider, worldState, codeinfoRepository, spec, tracer);

            gasAvailable = ilvmState.GasAvailable;
            programCounter = ilvmState.ProgramCounter;
            result = (ILChunkExecutionResult)ilvmState;
            stack.Head = ilvmState.StackHead;

            if (typeof(TTracingInstructions) == typeof(IsTracing))
                tracer.ReportOperationRemainingGas(gasAvailable);
            return true;
        }

        if (Mode.HasFlag(ILMode.PAT_MODE) && Chunks.TryGetValue(programCounter, out InstructionChunk patChunk))
        {
            var executionResult = new ILChunkExecutionResult();
            Metrics.IlvmPredefinedPatternsExecutions++;

            if (typeof(TTracingInstructions) == typeof(IsTracing))
                StartTracingSegment(in vmState, in stack, tracer, programCounter, gasAvailable, patChunk);

            patChunk.Invoke(vmState, blockHashProvider, worldState, codeinfoRepository, spec, ref programCounter, ref gasAvailable, ref stack, ref executionResult);

            if (typeof(TTracingInstructions) == typeof(IsTracing))
                tracer.ReportOperationRemainingGas(gasAvailable);

            result = executionResult;
            return true;
        }
        return false;
    }

    private static void StartTracingSegment<T, TTracingInstructions>(in EvmState vmState, in EvmStack<TTracingInstructions> stack, ITxTracer tracer, int programCounter, long gasAvailable, T chunk)
        where TTracingInstructions : struct, VirtualMachine.IIsTracing
    {
        if (chunk is PrecompiledChunk segment)
        {
            tracer.ReportCompiledSegmentExecution(gasAvailable, programCounter, segment.Name, vmState.Env);
        }
        else if (chunk is InstructionChunk patternHandler)
        {
            tracer.ReportPredefinedPatternExecution(gasAvailable, programCounter, patternHandler.Name, vmState.Env);
        }

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
