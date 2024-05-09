using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.IdentityModel.Tokens;
using Nethermind.Core.Specs;

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
        Segments = FrozenDictionary<ushort, Func<long, ILEvmState>>.Empty;
    }

    public IlInfo WithChunks(FrozenDictionary<ushort, InstructionChunk> chunks)
    {
        Chunks = chunks;
        return this;
    }

    public IlInfo WithSegments(FrozenDictionary<ushort, Func<long, ILEvmState>> segments)
    {
        Segments = segments;
        return this;
    }

    public IlInfo(FrozenDictionary<ushort, InstructionChunk> mappedOpcodes, FrozenDictionary<ushort, Func<long, ILEvmState>> segments)
    {
        Chunks = mappedOpcodes;
        Segments = segments;
    }

    // assumes small number of ILed
    public FrozenDictionary<ushort, InstructionChunk> Chunks { get; set; }
    public FrozenDictionary<ushort, Func<long, ILEvmState>> Segments { get; set; }

    public bool TryExecute<TTracingInstructions>(EvmState vmState, IReleaseSpec spec, ref int programCounter, ref long gasAvailable, ref EvmStack<TTracingInstructions> stack)
        where TTracingInstructions : struct, VirtualMachine.IIsTracing
    {
        if (programCounter > ushort.MaxValue)
            return false;

        switch(Mode)
        {
            case ILMode.PatternMatching:
                {
                    if (Chunks.TryGetValue((ushort)programCounter, out InstructionChunk chunk) == false)
                    {
                        return false;
                    }
                    chunk.Invoke(vmState, spec, ref programCounter, ref gasAvailable, ref stack);
                    break;
                }
            case ILMode.SubsegmentsCompiling:
                {
                    if (Segments.TryGetValue((ushort)programCounter, out Func<long, ILEvmState> method) == false)
                    {
                        return false;
                    }
                    var evmState = method.Invoke(gasAvailable);
                    // ToDo : Tidy up the exception handling
                    // ToDo : Add context switch, migrate stack from IL to EVM and map memory
                    // ToDo : Add context switch, prepare IL stack before executing the segment and map memory
                    break;  
                }
        }
        return true;
    }
}
