using System.Collections.Generic;
using System.Linq;

namespace Nethermind.Evm.IL;

/// <summary>
/// Represents an instruction <see cref="Instruction"/> with additional metadata required for a proper EVM construction.
/// </summary>
public readonly struct Operation
{
    /// <summary>
    /// The actual instruction
    /// </summary>
    public Instruction Instruction { get; }

    /// <summary>
    /// The gas cost.
    /// </summary>
    public long GasCost { get; }

    /// <summary>
    /// How many following bytes does this instruction have.
    /// </summary>
    public byte AdditionalBytes { get; }

    /// <summary>
    /// How many bytes are popped by this instruction.
    /// </summary>
    public byte StackBehaviorPop { get; }

    /// <summary>
    /// How many bytes are pushed by this instruction.
    /// </summary>
    public byte StackBehaviorPush { get; }

    /// <summary>
    /// Creates the new operation.
    /// </summary>
    public Operation(Instruction instruction, long gasCost, byte additionalBytes, byte stackBehaviorPop, byte stackBehaviorPush)
    {
        Instruction = instruction;
        GasCost = gasCost;
        AdditionalBytes = additionalBytes;
        StackBehaviorPop = stackBehaviorPop;
        StackBehaviorPush = stackBehaviorPush;
    }

    public static readonly IReadOnlyDictionary<Instruction, Operation> Operations =
        new Operation[]
        {
            new(Instruction.POP, GasCostOf.Base, 0, 1, 0),
            new(Instruction.PC, GasCostOf.Base, 0, 0, 1),
            new(Instruction.PUSH1, GasCostOf.VeryLow, 1, 0, 1),
            new(Instruction.PUSH2, GasCostOf.VeryLow, 2, 0, 1),
            new(Instruction.PUSH4, GasCostOf.VeryLow, 4, 0, 1),
            new(Instruction.JUMPDEST, GasCostOf.JumpDest, 0, 0, 0),
            new(Instruction.JUMP, GasCostOf.Mid, 0, 1, 0),
            new(Instruction.JUMPI, GasCostOf.High, 0, 2, 0),
            new(Instruction.SUB, GasCostOf.VeryLow, 0, 2, 1),
            new(Instruction.DUP1, GasCostOf.VeryLow, 0, 1, 2),
            new(Instruction.SWAP1, GasCostOf.VeryLow, 0, 2, 2)
        }.ToDictionary(op => op.Instruction);
}