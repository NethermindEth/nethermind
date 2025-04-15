// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FastEnumUtility;
using Nethermind.Core.Specs;
using Nethermind.Specs.Forks;

namespace Nethermind.Evm;

[SuppressMessage("ReSharper", "InconsistentNaming")]
public enum Instruction : byte
{
    STOP = 0x00,
    ADD = 0x01,
    MUL = 0x02,
    SUB = 0x03,
    DIV = 0x04,
    SDIV = 0x05,
    MOD = 0x06,
    SMOD = 0x07,
    ADDMOD = 0x08,
    MULMOD = 0x09,
    EXP = 0x0a,
    SIGNEXTEND = 0x0b,

    LT = 0x10,
    GT = 0x11,
    SLT = 0x12,
    SGT = 0x13,
    EQ = 0x14,
    ISZERO = 0x15,
    AND = 0x16,
    OR = 0x17,
    XOR = 0x18,
    NOT = 0x19,
    BYTE = 0x1a,
    SHL = 0x1b, // EIP-145
    SHR = 0x1c, // EIP-145
    SAR = 0x1d, // EIP-145

    KECCAK256 = 0x20,

    ADDRESS = 0x30,
    BALANCE = 0x31,
    ORIGIN = 0x32,
    CALLER = 0x33,
    CALLVALUE = 0x34,
    CALLDATALOAD = 0x35,
    CALLDATASIZE = 0x36,
    CALLDATACOPY = 0x37,
    CODESIZE = 0x38,
    CODECOPY = 0x39,
    GASPRICE = 0x3a,
    EXTCODESIZE = 0x3b,
    EXTCODECOPY = 0x3c,
    RETURNDATASIZE = 0x3d,
    RETURNDATACOPY = 0x3e,
    EXTCODEHASH = 0x3f,

    BLOCKHASH = 0x40,
    COINBASE = 0x41,
    TIMESTAMP = 0x42,
    NUMBER = 0x43,
    PREVRANDAO = 0x44,
    GASLIMIT = 0x45,
    CHAINID = 0x46,
    SELFBALANCE = 0x47,
    BASEFEE = 0x48,
    BLOBHASH = 0x49,
    BLOBBASEFEE = 0x4a,

    POP = 0x50,
    MLOAD = 0x51,
    MSTORE = 0x52,
    MSTORE8 = 0x53,
    SLOAD = 0x54,
    SSTORE = 0x55,
    JUMP = 0x56,
    JUMPI = 0x57,
    PC = 0x58,
    MSIZE = 0x59,
    GAS = 0x5a,
    JUMPDEST = 0x5b,
    TLOAD = 0x5c, // EIP-1153
    TSTORE = 0x5d,
    MCOPY = 0x5e,

    PUSH0 = 0x5f, // EIP-3855
    PUSH1 = 0x60,
    PUSH2 = 0x61,
    PUSH3 = 0x62,
    PUSH4 = 0x63,
    PUSH5 = 0x64,
    PUSH6 = 0x65,
    PUSH7 = 0x66,
    PUSH8 = 0x67,
    PUSH9 = 0x68,
    PUSH10 = 0x69,
    PUSH11 = 0x6a,
    PUSH12 = 0x6b,
    PUSH13 = 0x6c,
    PUSH14 = 0x6d,
    PUSH15 = 0x6e,
    PUSH16 = 0x6f,
    PUSH17 = 0x70,
    PUSH18 = 0x71,
    PUSH19 = 0x72,
    PUSH20 = 0x73,
    PUSH21 = 0x74,
    PUSH22 = 0x75,
    PUSH23 = 0x76,
    PUSH24 = 0x77,
    PUSH25 = 0x78,
    PUSH26 = 0x79,
    PUSH27 = 0x7a,
    PUSH28 = 0x7b,
    PUSH29 = 0x7c,
    PUSH30 = 0x7d,
    PUSH31 = 0x7e,
    PUSH32 = 0x7f,

    DUP1 = 0x80,
    DUP2 = 0x81,
    DUP3 = 0x82,
    DUP4 = 0x83,
    DUP5 = 0x84,
    DUP6 = 0x85,
    DUP7 = 0x86,
    DUP8 = 0x87,
    DUP9 = 0x88,
    DUP10 = 0x89,
    DUP11 = 0x8a,
    DUP12 = 0x8b,
    DUP13 = 0x8c,
    DUP14 = 0x8d,
    DUP15 = 0x8e,
    DUP16 = 0x8f,

    SWAP1 = 0x90,
    SWAP2 = 0x91,
    SWAP3 = 0x92,
    SWAP4 = 0x93,
    SWAP5 = 0x94,
    SWAP6 = 0x95,
    SWAP7 = 0x96,
    SWAP8 = 0x97,
    SWAP9 = 0x98,
    SWAP10 = 0x99,
    SWAP11 = 0x9a,
    SWAP12 = 0x9b,
    SWAP13 = 0x9c,
    SWAP14 = 0x9d,
    SWAP15 = 0x9e,
    SWAP16 = 0x9f,

    LOG0 = 0xa0,
    LOG1 = 0xa1,
    LOG2 = 0xa2,
    LOG3 = 0xa3,
    LOG4 = 0xa4,

    DATALOAD = 0xd0,
    DATALOADN = 0xd1,
    DATASIZE = 0xd2,
    DATACOPY = 0xd3,

    RJUMP = 0xe0,
    RJUMPI = 0xe1,
    RJUMPV = 0xe2,
    CALLF = 0xe3,
    RETF = 0xe4,
    JUMPF = 0xe5,
    DUPN = 0xe6,
    SWAPN = 0xe7,
    EXCHANGE = 0xe8,

    EOFCREATE = 0xec,

    RETURNCODE = 0xee,

    CREATE = 0xf0,
    CALL = 0xf1,
    CALLCODE = 0xf2,
    RETURN = 0xf3,
    DELEGATECALL = 0xf4,
    CREATE2 = 0xf5,
    RETURNDATALOAD = 0xf7,
    EXTCALL = 0xf8,
    EXTDELEGATECALL = 0xf9,
    STATICCALL = 0xfa,
    EXTSTATICCALL = 0xfb,
    REVERT = 0xfd,
    INVALID = 0xfe,
    SELFDESTRUCT = 0xff
}

public static class InstructionExtensions
{
    private readonly static bool[] _terminatingInstructions = CreateTerminatingInstructionsLookup();
    private readonly static bool[] _validLegacyInstructions = CreateValidInstructionsLookup(isEofContext: false);
    private readonly static bool[] _validEofInstructions = CreateValidInstructionsLookup(isEofContext: true);

    public static bool IsTerminating(this Instruction instruction)
        => Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_terminatingInstructions), (int)instruction);

    public static bool IsValid(this Instruction instruction, bool isEofContext)
        => Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(isEofContext ? _validEofInstructions : _validLegacyInstructions), (int)instruction);

    public static (ushort InputCount, ushort OutputCount, ushort immediates) StackRequirements(this Instruction instruction)
    {
        switch (instruction)
        {
            case Instruction.STOP:
            case Instruction.INVALID:
            case Instruction.JUMPDEST:
            case Instruction.RETF:
                return (0, 0, 0);
            case Instruction.POP:
            case Instruction.SELFDESTRUCT:
            case Instruction.JUMP:
                return (1, 0, 0);
            case Instruction.ISZERO:
            case Instruction.NOT:
            case Instruction.BALANCE:
            case Instruction.CALLDATALOAD:
            case Instruction.EXTCODESIZE:
            case Instruction.RETURNDATALOAD:
            case Instruction.EXTCODEHASH:
            case Instruction.BLOCKHASH:
            case Instruction.MLOAD:
            case Instruction.SLOAD:
            case Instruction.BLOBHASH:
            case Instruction.TLOAD:
            case Instruction.DATALOAD:
                return (1, 1, 0);
            case Instruction.MSTORE:
            case Instruction.MSTORE8:
            case Instruction.SSTORE:
            case Instruction.LOG0:
            case Instruction.REVERT:
            case Instruction.TSTORE:
            case Instruction.RETURN:
            case Instruction.JUMPI:
                return (2, 0, 0);
            case Instruction.RETURNCODE:
                return (2, 2, 1);
            case Instruction.CALLDATACOPY:
            case Instruction.CODECOPY:
            case Instruction.RETURNDATACOPY:
            case Instruction.LOG1:
            case Instruction.DATACOPY:
            case Instruction.MCOPY:
                return (3, 0, 0);
            case Instruction.EXTCODECOPY:
            case Instruction.LOG2:
                return (4, 0, 0);
            case Instruction.LOG3:
                return (5, 0, 0);
            case Instruction.LOG4:
                return (6, 0, 0);
            case Instruction.ADDMOD:
            case Instruction.MULMOD:
            case Instruction.CREATE:
            case Instruction.EXTSTATICCALL:
            case Instruction.EXTDELEGATECALL:
                return (3, 1, 0);
            case Instruction.CREATE2:
            case Instruction.EXTCALL:
                return (4, 1, 0);
            case Instruction.EOFCREATE:
                return (4, 1, 1);
            case Instruction.ADDRESS:
            case Instruction.ORIGIN:
            case Instruction.CALLER:
            case Instruction.CALLVALUE:
            case Instruction.CALLDATASIZE:
            case Instruction.CODESIZE:
            case Instruction.GASPRICE:
            case Instruction.RETURNDATASIZE:
            case Instruction.COINBASE:
            case Instruction.TIMESTAMP:
            case Instruction.NUMBER:
            case Instruction.PREVRANDAO:
            case Instruction.GASLIMIT:
            case Instruction.CHAINID:
            case Instruction.SELFBALANCE:
            case Instruction.BASEFEE:
            case Instruction.MSIZE:
            case Instruction.GAS:
            case Instruction.PC:
            case Instruction.BLOBBASEFEE:
            case Instruction.DATASIZE:
                return (0, 1, 0);
            case Instruction.RJUMP:
            case Instruction.CALLF:
            case Instruction.JUMPF:
                return (0, 0, 2);
            case Instruction.DATALOADN:
                return (0, 1, 2);
            case Instruction.RJUMPI:
                return (1, 0, 2);
            case Instruction.CALL:
            case Instruction.DELEGATECALL:
            case Instruction.STATICCALL:
            case Instruction.CALLCODE:
                return (6, 1, 0);
            case >= Instruction.PUSH0 and <= Instruction.PUSH32:
                return (0, 1, instruction - Instruction.PUSH0);
            case >= Instruction.DUP1 and <= Instruction.DUP16:
                return ((ushort)(instruction - Instruction.DUP1 + 1), (ushort)(instruction - Instruction.DUP1 + 2), 0);
            case >= Instruction.SWAP1 and <= Instruction.SWAP16:
                return ((ushort)(instruction - Instruction.SWAP1 + 2), (ushort)(instruction - Instruction.SWAP1 + 2), 0);
            case Instruction.RJUMPV:
                // multi-bytes opcode
                return (1, 0, 0);
            // multi-bytes opcode
            case Instruction.SWAPN:
            case Instruction.DUPN:
            case Instruction.EXCHANGE:
                return (0, 0, 1);
            default:
                return Enum.IsDefined(instruction) ? ((ushort)2, (ushort)1, (ushort)0) : ThrowNotImplemented(instruction);
        }
    }

    [DoesNotReturn]
    [StackTraceHidden]
    private static (ushort InputCount, ushort OutputCount, ushort immediates) ThrowNotImplemented(Instruction instruction)
        => throw new NotImplementedException($"opcode {instruction} not implemented yet");

    private static bool[] CreateValidInstructionsLookup(bool isEofContext)
    {
        bool[] instructions = new bool[byte.MaxValue];
        for (int i = 0; i < instructions.Length; i++)
        {
            instructions[i] = IsValidInstruction((Instruction)i, isEofContext);
        }
        return instructions;
    }

    private static bool[] CreateTerminatingInstructionsLookup()
    {
        bool[] instructions = new bool[byte.MaxValue];

        instructions[(int)Instruction.STOP] = true;
        instructions[(int)Instruction.RJUMP] = true;
        instructions[(int)Instruction.RETF] = true;
        instructions[(int)Instruction.JUMPF] = true;
        instructions[(int)Instruction.RETURNCODE] = true;
        instructions[(int)Instruction.RETURN] = true;
        instructions[(int)Instruction.REVERT] = true;
        instructions[(int)Instruction.INVALID] = true;

        return instructions;
    }

    private static bool IsValidInstruction(Instruction instruction, bool isEofContext)
    {
        if (!Enum.IsDefined(instruction))
        {
            return false;
        }

        return instruction switch
        {
            Instruction.CALLF or Instruction.RETF or Instruction.JUMPF => isEofContext,
            Instruction.DUPN or Instruction.SWAPN or Instruction.EXCHANGE => isEofContext,
            Instruction.RJUMP or Instruction.RJUMPI or Instruction.RJUMPV => isEofContext,
            Instruction.RETURNCODE or Instruction.EOFCREATE => isEofContext,
            Instruction.DATACOPY or Instruction.DATASIZE or Instruction.DATALOAD or Instruction.DATALOADN => isEofContext,
            Instruction.EXTSTATICCALL or Instruction.EXTDELEGATECALL or Instruction.EXTCALL => isEofContext,
            Instruction.RETURNDATALOAD => isEofContext,
            Instruction.CALL => !isEofContext,
            Instruction.CALLCODE => !isEofContext,
            Instruction.DELEGATECALL => !isEofContext,
            Instruction.STATICCALL => !isEofContext,
            Instruction.SELFDESTRUCT => !isEofContext,
            Instruction.JUMP => !isEofContext,
            Instruction.JUMPI => !isEofContext,
            Instruction.PC => !isEofContext,
            Instruction.CREATE2 or Instruction.CREATE => !isEofContext,
            Instruction.CODECOPY => !isEofContext,
            Instruction.CODESIZE => !isEofContext,
            Instruction.EXTCODEHASH => !isEofContext,
            Instruction.EXTCODECOPY => !isEofContext,
            Instruction.EXTCODESIZE => !isEofContext,
            Instruction.GAS => !isEofContext,
            _ => true
        };
    }

    public static string? GetName(this Instruction instruction, bool isPostMerge = false, IReleaseSpec? spec = null)
    {
        spec ??= Frontier.Instance;
        return instruction switch
        {
            Instruction.PREVRANDAO when !isPostMerge => "DIFFICULTY",
            Instruction.JUMPDEST => spec.IsEofEnabled ? "NOP" : "JUMPDEST",
            _ => FastEnum.IsDefined(instruction) ? FastEnum.GetName(instruction) : null,
        };
    }
}
