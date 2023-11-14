using System;
using System.Runtime.CompilerServices;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.CodeAnalysis.IL;

/// <summary>
/// Represents the IL-EVM information about the contract.
/// </summary>
internal class IlInfo
{
    private const int NotFound = -1;

    /// <summary>
    /// Represents an information about IL-EVM being not able to optimize the given <see cref="CodeInfo"/>.
    /// </summary>
    public static readonly IlInfo NoIlEVM = new();

    /// <summary>
    /// No overrides.
    /// </summary>
    private IlInfo()
    {
        _pCs = Array.Empty<ushort>();
        _chunks = Array.Empty<InstructionChunk>();
    }

    public IlInfo(ushort[] pcs, InstructionChunk[] chunks)
    {
        _pCs = pcs;
        _chunks = chunks;
    }

    // assumes small number of ILed
    private readonly ushort[] _pCs;
    private readonly InstructionChunk[] _chunks;

    public bool TryExecute<TTracingInstructions>(EvmState vmState, IReleaseSpec spec, ref int programCounter, ref long gasAvailable, ref EvmStack<TTracingInstructions> stack)
        where TTracingInstructions : struct, VirtualMachine.IIsTracing
    {
        if (programCounter > ushort.MaxValue)
            return false;

        int at = _pCs.AsSpan().IndexOf((ushort)programCounter);
        if (at == NotFound)
            return false;

        _chunks[at](vmState, spec, ref programCounter, ref gasAvailable, ref stack);
        return true;
    }
}
