using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.IdentityModel.Tokens;
using Nethermind.Core;
using Nethermind.Core.Specs;
using static Nethermind.Evm.CodeAnalysis.IL.ILCompiler;

namespace Nethermind.Evm.CodeAnalysis.IL;
/// <summary>
/// Represents the IL-EVM information about the contract.
/// </summary>
internal class IlInfo
{
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
    public bool IsEmpty => Chunks.IsNullOrEmpty() && Segments.IsNullOrEmpty();
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

    public bool TryExecute<TTracingInstructions>(EvmState vmState, BlockExecutionContext blkCtx, TxExecutionContext txCtx, ISpecProvider specProvider, ref int programCounter, ref long gasAvailable, ref EvmStack<TTracingInstructions> stack, out bool shouldStop, out bool shouldRevert, out bool shouldReturn, out object returnData)
        where TTracingInstructions : struct, VirtualMachine.IIsTracing
    {
        shouldReturn = false;
        shouldRevert = false;
        shouldStop = false;
        returnData = null;
        if (programCounter > ushort.MaxValue)
            return false;

        switch (Mode)
        {
            case ILMode.PatternMatching:
                {
                    if (Chunks.TryGetValue((ushort)programCounter, out InstructionChunk chunk) == false)
                    {
                        return false;
                    }
                    chunk.Invoke(vmState, specProvider.GetSpec(blkCtx.Header.Number, blkCtx.Header.Timestamp), ref programCounter, ref gasAvailable, ref stack);
                    break;
                }
            case ILMode.SubsegmentsCompiling:
                {
                    if (Segments.TryGetValue((ushort)programCounter, out SegmentExecutionCtx ctx) == false)
                    {
                        return false;
                    }

                    var ilvmState = new ILEvmState
                    {
                        GasAvailable = (int)gasAvailable,
                        Stack = vmState.DataStack,
                        Env = vmState.Env,
                        BlkCtx = blkCtx,
                        TxCtx = txCtx,
                        ProgramCounter = (ushort)programCounter,
                        Memory = ref vmState.Memory,
                    };

                    ctx.Method.Invoke(ref ilvmState, specProvider, ctx.Data);
                    gasAvailable = ilvmState.GasAvailable;
                    vmState.DataStack = ilvmState.Stack.ToArray();
                    programCounter = ilvmState.ProgramCounter;
                    shouldStop = ilvmState.ShouldStop;
                    shouldReturn = ilvmState.ShouldReturn;
                    shouldRevert = ilvmState.ShouldRevert;
                    returnData = ilvmState.ReturnBuffer;
                    break;
                }
        }
        return true;
    }
}
