using System;
using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.CodeAnalysis.IL;

/// <summary>
/// Represents the IL-EVM information about the contract.
/// </summary>
internal class IlInfo
{

    /// <summary>
    /// Represents an information about IL-EVM being not able to optimize the given <see cref="CodeInfo"/>.
    /// </summary>
    public static readonly IlInfo NoIlEVM = new();

    /// <summary>
    /// No overrides.
    /// </summary>
    private IlInfo()
    {
        Chunks = FrozenDictionary<ushort, InstructionChunk>.Empty;
    }

    public IlInfo(FrozenDictionary<ushort, InstructionChunk> mappedOpcodes)
    {
        Chunks = mappedOpcodes;
    }

    // assumes small number of ILed
    public FrozenDictionary<ushort, InstructionChunk> Chunks { get; init; }

    public bool TryExecute<TTracingInstructions>(EvmState vmState, IReleaseSpec spec, ref int programCounter, ref long gasAvailable, ref EvmStack<TTracingInstructions> stack)
        where TTracingInstructions : struct, VirtualMachine.IIsTracing
    {
        if (programCounter > ushort.MaxValue)
            return false;

        bool hasProgramCounter = Chunks.ContainsKey((ushort)programCounter);
        if (!hasProgramCounter)
            return false;

        Chunks[(ushort)programCounter].Invoke(vmState, spec, ref programCounter, ref gasAvailable, ref stack);
        return true;
    }
}
