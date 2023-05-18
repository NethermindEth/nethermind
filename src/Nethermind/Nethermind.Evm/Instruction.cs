// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using FastEnumUtility;
using Nethermind.Core.Specs;
using Nethermind.Evm.EOF;
using Nethermind.Specs.Forks;

namespace Nethermind.Evm
{
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
        SHL = 0x1b, // ShiftOpcodesEnabled
        SHR = 0x1c, // ShiftOpcodesEnabled
        SAR = 0x1d, // ShiftOpcodesEnabled

        SHA3 = 0x20,

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
        RETURNDATASIZE = 0x3d, // ReturnDataOpcodesEnabled
        RETURNDATACOPY = 0x3e, // ReturnDataOpcodesEnabled
        EXTCODEHASH = 0x3f, // ExtCodeHashOpcodeEnabled

        BLOCKHASH = 0x40,
        COINBASE = 0x41,
        TIMESTAMP = 0x42,
        NUMBER = 0x43,
        PREVRANDAO = 0x44,
        GASLIMIT = 0x45,
        CHAINID = 0x46, // ChainIdOpcodeEnabled
        SELFBALANCE = 0x47, // SelfBalanceOpcodeEnabled
        BASEFEE = 0x48, // BaseFeeEnabled
        DATAHASH = 0x49,

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
        NOP = 0x5b,
        JUMPDEST = 0x5b,
        RJUMP = 0x5c, // RelativeStaticJumps
        RJUMPI = 0x5d, // RelativeStaticJumps
        RJUMPV = 0x5e, // RelativeStaticJumps
        BEGINSUB = 0x5c, // SubroutinesEnabled
        RETURNSUB = 0x5d, // SubroutinesEnabled
        JUMPSUB = 0x5e, // SubroutinesEnabled

        PUSH0 = 0x5f, // IncludePush0Instruction
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

        DUPN = 0xb5,
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

        SWAPN = 0xb6,
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

        // EIP-1153
        TLOAD = 0xb3, // TransientStorageEnabled
        TSTORE = 0xb4, //TransientStorageEnabled

        CREATE = 0xf0,
        CALL = 0xf1,
        CALLF = 0xb0, // FunctionSection
        RETF = 0xb1, // FunctionSection
        CALLCODE = 0xf2,
        RETURN = 0xf3,
        DELEGATECALL = 0xf4, // DelegateCallEnabled
        CREATE2 = 0xf5, // Create2OpcodeEnabled
        STATICCALL = 0xfa, // StaticCallEnabled
        REVERT = 0xfd, // RevertOpcodeEnabled
        INVALID = 0xfe,
        SELFDESTRUCT = 0xff,
    }

    public static class InstructionExtensions
    {
        public static int GetImmediateCount(this Instruction instruction, bool IsEofContext, byte jumpvCount = 0)
            => instruction switch
            {
                Instruction.DUPN or Instruction.SWAPN => IsEofContext ? EvmObjectFormat.Eof1.ONE_BYTE_LENGTH : 0,
                Instruction.CALLF => IsEofContext ? EvmObjectFormat.Eof1.TWO_BYTE_LENGTH : 0,
                Instruction.RJUMP or Instruction.RJUMPI => IsEofContext ? EvmObjectFormat.Eof1.TWO_BYTE_LENGTH : 0,
                Instruction.RJUMPV => IsEofContext ? jumpvCount * EvmObjectFormat.Eof1.TWO_BYTE_LENGTH + EvmObjectFormat.Eof1.ONE_BYTE_LENGTH : 0,
                >= Instruction.PUSH0 and <= Instruction.PUSH32 => instruction - Instruction.PUSH0,
                _ => 0
            };
        public static bool IsTerminating(this Instruction instruction) => instruction switch
        {
            Instruction.RETF or Instruction.INVALID or Instruction.STOP or Instruction.RETURN or Instruction.REVERT => true,
            // Instruction.SELFDESTRUCT => true
            _ => false
        };

        public static bool IsValid(this Instruction instruction, bool IsEofContext)
        {
            if (!Enum.IsDefined(instruction))
            {
                return false;
            }

            return instruction switch
            {
                Instruction.PC => !IsEofContext,
                Instruction.CALLCODE or Instruction.SELFDESTRUCT => !IsEofContext,
                Instruction.JUMPI or Instruction.JUMP => !IsEofContext,
                Instruction.CALLF or Instruction.RETF => IsEofContext,
                Instruction.DUPN or Instruction.SWAPN => IsEofContext,
                Instruction.BEGINSUB or Instruction.RETURNSUB or Instruction.JUMPSUB => true,
                _ => true
            };
        }

        //Note() : Extensively test this, refactor it, 
        public static (ushort? InputCount, ushort? OutputCount, ushort? immediates) StackRequirements(this Instruction instruction) => instruction switch
        {
            Instruction.STOP => (0, 0, 0),
            Instruction.ADD => (2, 1, 0),
            Instruction.MUL => (2, 1, 0),
            Instruction.SUB => (2, 1, 0),
            Instruction.DIV => (2, 1, 0),
            Instruction.SDIV => (2, 1, 0),
            Instruction.MOD => (2, 1, 0),
            Instruction.SMOD => (2, 1, 0),
            Instruction.ADDMOD => (3, 1, 0),
            Instruction.MULMOD => (3, 1, 0),
            Instruction.EXP => (2, 1, 0),
            Instruction.SIGNEXTEND => (2, 1, 0),
            Instruction.LT => (2, 1, 0),
            Instruction.GT => (2, 1, 0),
            Instruction.SLT => (2, 1, 0),
            Instruction.SGT => (2, 1, 0),
            Instruction.EQ => (2, 1, 0),
            Instruction.ISZERO => (1, 1, 0),
            Instruction.AND => (2, 1, 0),
            Instruction.OR => (2, 1, 0),
            Instruction.XOR => (2, 1, 0),
            Instruction.NOT => (1, 1, 0),
            Instruction.BYTE => (2, 1, 0),
            Instruction.SHL => (2, 1, 0),
            Instruction.SHR => (2, 1, 0),
            Instruction.SAR => (2, 1, 0),
            Instruction.SHA3 => (2, 1, 0),
            Instruction.ADDRESS => (0, 1, 0),
            Instruction.BALANCE => (1, 1, 0),
            Instruction.ORIGIN => (0, 1, 0),
            Instruction.CALLER => (0, 1, 0),
            Instruction.CALLVALUE => (0, 1, 0),
            Instruction.CALLDATALOAD => (1, 1, 0),
            Instruction.CALLDATASIZE => (0, 1, 0),
            Instruction.CALLDATACOPY => (3, 0, 0),
            Instruction.CODESIZE => (0, 1, 0),
            Instruction.CODECOPY => (3, 0, 0),
            Instruction.GASPRICE => (0, 1, 0),
            Instruction.EXTCODESIZE => (1, 1, 0),
            Instruction.EXTCODECOPY => (4, 0, 0),
            Instruction.RETURNDATASIZE => (0, 1, 0),
            Instruction.RETURNDATACOPY => (3, 0, 0),
            Instruction.EXTCODEHASH => (1, 1, 0),
            Instruction.BLOCKHASH => (1, 1, 0),
            Instruction.COINBASE => (0, 1, 0),
            Instruction.TIMESTAMP => (0, 1, 0),
            Instruction.NUMBER => (0, 1, 0),
            Instruction.PREVRANDAO => (0, 1, 0),
            Instruction.GASLIMIT => (0, 1, 0),
            Instruction.CHAINID => (0, 1, 0),
            Instruction.SELFBALANCE => (0, 1, 0),
            Instruction.BASEFEE => (0, 1, 0),
            Instruction.POP => (1, 0, 0),
            Instruction.MLOAD => (1, 1, 0),
            Instruction.MSTORE => (2, 0, 0),
            Instruction.MSTORE8 => (2, 0, 0),
            Instruction.SLOAD => (1, 1, 0),
            Instruction.SSTORE => (2, 0, 0),
            Instruction.MSIZE => (0, 1, 0),
            Instruction.GAS => (0, 1, 0),
            Instruction.JUMPDEST => (0, 0, 0),
            Instruction.RJUMP => (0, 0, 2),
            Instruction.RJUMPI => (1, 0, 2),
            Instruction.DATAHASH => (1, 1, 0),
            >= Instruction.PUSH0 and <= Instruction.PUSH32 => (0, 1, instruction - Instruction.PUSH0),
            >= Instruction.DUP1 and <= Instruction.DUP16 => ((ushort)(instruction - Instruction.DUP1 + 1), (ushort)(instruction - Instruction.DUP1 + 2), 0),
            >= Instruction.SWAP1 and <= Instruction.SWAP16 => ((ushort)(instruction - Instruction.SWAP1 + 2), (ushort)(instruction - Instruction.SWAP1 + 2), 0),
            Instruction.LOG0 => (2, 0, 0),
            Instruction.LOG1 => (3, 0, 0),
            Instruction.LOG2 => (4, 0, 0),
            Instruction.LOG3 => (5, 0, 0),
            Instruction.LOG4 => (6, 0, 0),
            Instruction.CALLF => (0, 0, 2),
            Instruction.RETF => (0, 0, 0),
            Instruction.CREATE => (3, 1, 0),
            Instruction.CALL => (7, 1, 0),
            Instruction.RETURN => (2, 0, 0),
            Instruction.DELEGATECALL => (6, 1, 0),
            Instruction.CREATE2 => (4, 1, 0),
            Instruction.STATICCALL => (6, 1, 0),
            Instruction.REVERT => (2, 0, 0),
            Instruction.INVALID => (0, 0, 0),
            Instruction.RJUMPV => (1, 0, null), // null indicates this is a dynamic multi-bytes opcode
            Instruction.SWAPN => (null, null, 1),
            Instruction.DUPN => (null, null, 1),
            _ => throw new NotImplementedException($"Instruction {instruction} not implemented")
        };

        public static string? GetName(this Instruction instruction, bool isPostMerge = false, IReleaseSpec? spec = null)
        {
            spec ??= Frontier.Instance;
            return instruction switch
            {
                Instruction.PREVRANDAO => isPostMerge ? "PREVRANDAO" : "DIFFICULTY",
                Instruction.RJUMP => !spec.StaticRelativeJumpsEnabled ? "BEGINSUB" : "RJUMP",
                Instruction.RJUMPI => spec.StaticRelativeJumpsEnabled ? "RJUMPI" : "RETURNSUB",
                Instruction.RJUMPV => spec.StaticRelativeJumpsEnabled ? "RJUMPV" : "JUMPSUB",
                Instruction.JUMPDEST => spec.FunctionSections ? "NOP" : "JUMPDEST",
                _ => FastEnum.IsDefined(instruction) ? FastEnum.GetName(instruction) : null,
            };
        }
    }
}
