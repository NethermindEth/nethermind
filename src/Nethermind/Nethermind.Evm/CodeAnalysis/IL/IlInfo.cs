using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.IdentityModel.Tokens;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.State;
using NonBlocking;
using static Nethermind.Evm.CodeAnalysis.IL.ILCompiler;
using static Nethermind.Evm.VirtualMachine;

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
    }

    public enum ILMode
    {
        NoIlvm = 0,
        PatternMatching = 1,
        SubsegmentsCompiling = 2
    }

    /// <summary>
    /// Represents an information about IL-EVM being not able to optimize the given <see cref="CodeInfo"/>.
    /// </summary>
    public static IlInfo Empty => new();

    /// <summary>
    /// Represents what mode of IL-EVM is used. 0 is the default. [0 = No ILVM optimizations, 1 = Pattern matching, 2 = subsegments compiling]
    /// </summary>
    public ILMode Mode = ILMode.NoIlvm;
    public bool IsEmpty => Chunks.Count == 0 && Segments.Count == 0;
    /// <summary>
    /// No overrides.
    /// </summary>
    private IlInfo()
    {
        Chunks = new ConcurrentDictionary<ushort, InstructionChunk>();
        Segments = new ConcurrentDictionary<ushort, SegmentExecutionCtx>();
    }

    public IlInfo(ConcurrentDictionary<ushort, InstructionChunk> mappedOpcodes, ConcurrentDictionary<ushort, SegmentExecutionCtx> segments)
    {
        Chunks = mappedOpcodes;
        Segments = segments;
    }

    // assumes small number of ILed
    public ConcurrentDictionary<ushort, InstructionChunk> Chunks { get; } = new();
    public ConcurrentDictionary<ushort, SegmentExecutionCtx> Segments { get; } = new();

    public bool TryExecute<TTracingInstructions>(EvmState vmState, ulong chainId, ref ReadOnlyMemory<byte> outputBuffer, IWorldState worldState, IBlockhashProvider blockHashProvider, ICodeInfoRepository codeinfoRepository, IReleaseSpec spec, ITxTracer tracer, ref int programCounter, ref long gasAvailable, ref EvmStack<TTracingInstructions> stack, out ILChunkExecutionResult? result)
        where TTracingInstructions : struct, VirtualMachine.IIsTracing
    {
        result = null;
        if (programCounter > ushort.MaxValue || Mode == ILMode.NoIlvm)
            return false;

        var executionResult = new ILChunkExecutionResult();
        if (Mode.HasFlag(ILMode.SubsegmentsCompiling) && Segments.TryGetValue((ushort)programCounter, out SegmentExecutionCtx ctx))
        {
            vmState.DataStackHead = stack.Head;

            if (typeof(TTracingInstructions) == typeof(IsTracing))
                StartTracingSegment(in vmState, in stack, tracer, programCounter, gasAvailable, ctx);

            var ilvmState = new ILEvmState(chainId, vmState, EvmExceptionType.None, (ushort)programCounter, gasAvailable, ref outputBuffer);

            ctx.PrecompiledSegment.Invoke(ref ilvmState, blockHashProvider, worldState, codeinfoRepository, spec, ctx.Data);

            gasAvailable = ilvmState.GasAvailable;
            programCounter = ilvmState.ProgramCounter;

            executionResult.ShouldReturn = ilvmState.ShouldReturn;
            executionResult.ShouldRevert = ilvmState.ShouldRevert;
            executionResult.ShouldStop = ilvmState.ShouldStop;
            executionResult.ShouldJump = ilvmState.ShouldJump;
            executionResult.ExceptionType = ilvmState.EvmException;
            executionResult.ReturnData = ilvmState.ReturnBuffer;

            stack.Head = ilvmState.StackHead;

            if (typeof(TTracingInstructions) == typeof(IsTracing))
                tracer.ReportOperationRemainingGas(gasAvailable);
        }
        else if (Mode.HasFlag(ILMode.PatternMatching) && Chunks.TryGetValue((ushort)programCounter, out InstructionChunk chunk))
        {
            if (typeof(TTracingInstructions) == typeof(IsTracing))
                StartTracingSegment(in vmState, in stack, tracer, programCounter, gasAvailable, chunk);

            chunk.Invoke(vmState, blockHashProvider, worldState, codeinfoRepository, spec, ref programCounter, ref gasAvailable, ref stack, ref executionResult);

            if (typeof(TTracingInstructions) == typeof(IsTracing))
                tracer.ReportOperationRemainingGas(gasAvailable);
        }
        else
        {
            return false;
        }

        result = executionResult;
        return true;
    }

    private static void StartTracingSegment<T, TTracingInstructions>(in EvmState vmState, in EvmStack<TTracingInstructions> stack, ITxTracer tracer, int programCounter, long gasAvailable, T chunk)
        where TTracingInstructions : struct, VirtualMachine.IIsTracing
    {
        if(chunk is SegmentExecutionCtx segment)
        {
            tracer.ReportCompiledSegmentExecution(gasAvailable, programCounter, segment.Name, vmState.Env);
        }
        else if(chunk is InstructionChunk patternHandler)
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
