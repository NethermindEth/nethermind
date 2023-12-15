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
    public static FrozenDictionary<byte[], InstructionChunk> Patterns { get; } = FrozenDictionary<byte[], InstructionChunk>.Empty;

    /// <summary>
    /// Represents an information about IL-EVM being not able to optimize the given <see cref="CodeInfo"/>.
    /// </summary>
    public static readonly IlInfo NoIlEVM = new();

    /// <summary>
    /// No overrides.
    /// </summary>
    private IlInfo()
    {
        _chunks = FrozenDictionary<ushort, InstructionChunk>.Empty;
    }

    public IlInfo(FrozenDictionary<ushort, InstructionChunk> mappedOpcodes)
    {
        _chunks = mappedOpcodes;
    }

    // assumes small number of ILed
    private readonly FrozenDictionary<ushort, InstructionChunk> _chunks;

    public bool TryExecute<TTracingInstructions>(EvmState vmState, IReleaseSpec spec, ref int programCounter, ref long gasAvailable, ref EvmStack<TTracingInstructions> stack)
        where TTracingInstructions : struct, VirtualMachine.IIsTracing
    {
        if (programCounter > ushort.MaxValue)
            return false;

        bool hasProgramCounter = _chunks.ContainsKey((ushort)programCounter);
        if (!hasProgramCounter)
            return false;

        _chunks[(ushort)programCounter].Invoke(vmState, spec, ref programCounter, ref gasAvailable, ref stack);
        return true;
    }
}
