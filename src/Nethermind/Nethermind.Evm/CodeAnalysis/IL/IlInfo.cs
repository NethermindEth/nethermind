using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.IdentityModel.Tokens;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.State;
using static Nethermind.Evm.CodeAnalysis.IL.ILCompiler;

namespace Nethermind.Evm.CodeAnalysis.IL;
/// <summary>
/// Represents the IL-EVM information about the contract.
/// </summary>
internal class IlInfo
{
    internal struct ILChunkExecutionResult
    {
        public bool ShouldAbort;
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
    /// Represents what mode of IL-EVM is used. 0 is the default. [0 = Pattern matching, 1 = subsegments compiling]
    /// </summary>
    public static readonly ILMode Mode = ILMode.PatternMatching;
    public bool IsEmpty => Chunks.Count == 0 && Segments.Count == 0;
    /// <summary>
    /// No overrides.
    /// </summary>
    private IlInfo()
    {
        Chunks = FrozenDictionary<ushort, InstructionChunk>.Empty;
        Segments = FrozenDictionary<ushort, SegmentExecutionCtx>.Empty;
    }

    public IlInfo WithChunks(FrozenDictionary<ushort, InstructionChunk> chunks)
    {
        Chunks = chunks;
        return this;
    }

    public IlInfo WithSegments(FrozenDictionary<ushort, SegmentExecutionCtx> segments)
    {
        Segments = segments;
        return this;
    }

    public IlInfo(FrozenDictionary<ushort, InstructionChunk> mappedOpcodes, FrozenDictionary<ushort, SegmentExecutionCtx> segments)
    {
        Chunks = mappedOpcodes;
        Segments = segments;
    }

    // assumes small number of ILed
    public FrozenDictionary<ushort, InstructionChunk> Chunks { get; set; }
    public FrozenDictionary<ushort, SegmentExecutionCtx> Segments { get; set; }

    public bool TryExecute<TTracingInstructions>(EvmState vmState, ulong chainId, ref ReadOnlyMemory<byte> outputBuffer, IWorldState worldState, IBlockhashProvider blockHashProvider, ICodeInfoRepository codeinfoRepository, IReleaseSpec spec, ITxTracer tracer, ref int programCounter, ref long gasAvailable, ref EvmStack<TTracingInstructions> stack, out ILChunkExecutionResult? result)
        where TTracingInstructions : struct, VirtualMachine.IIsTracing
    {
        result = null;
        if (programCounter > ushort.MaxValue)
            return false;

        var executionResult = new ILChunkExecutionResult();
        if (Segments.TryGetValue((ushort)programCounter, out SegmentExecutionCtx ctx))
        {
            tracer.ReportCompiledSegmentExecution(gasAvailable, programCounter, ctx.Method.Method.Name);
            var ilvmState = new ILEvmState(chainId, vmState, EvmExceptionType.None, (ushort)programCounter, gasAvailable, ref outputBuffer);

            ctx.Method.Invoke(ref ilvmState, blockHashProvider, worldState, codeinfoRepository, spec, ctx.Data);

            gasAvailable = ilvmState.GasAvailable;
            programCounter = ilvmState.ProgramCounter;

            executionResult.ShouldReturn = ilvmState.ShouldReturn;
            executionResult.ShouldRevert = ilvmState.ShouldRevert;
            executionResult.ShouldStop = ilvmState.ShouldStop;
            executionResult.ShouldAbort = ilvmState.EvmException != EvmExceptionType.None;
            executionResult.ShouldJump = ilvmState.ShouldJump;
            executionResult.ExceptionType = ilvmState.EvmException;
            executionResult.ReturnData = ilvmState.ReturnBuffer;
        } else if(Chunks.TryGetValue((ushort)programCounter, out InstructionChunk chunk))
        {
            tracer.ReportChunkExecution(gasAvailable, programCounter, chunk.GetType().Name);
            var evmException = chunk.Invoke(vmState, worldState, spec, ref programCounter, ref gasAvailable, ref stack);
            executionResult.ShouldAbort = evmException != EvmExceptionType.None;
            executionResult.ExceptionType = evmException;
        } else
        {
            return false;
        }

        result = executionResult;
        return true;
    }
}
