// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using FastEnumUtility;
using Nethermind.Core.Specs;
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
        RJUMP = 0x5c, // RelativeStaticJumps
        RJUMPI = 0x5d, // RelativeStaticJumps
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

        // EIP-1153
        TLOAD = 0xb3, // TransientStorageEnabled
        TSTORE = 0xb4, //TransientStorageEnabled

        CREATE = 0xf0,
        CALL = 0xf1,
        RETF = 0xb1, // FunctionSection
        CALLF = 0xb0, // FunctionSection
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
        public static bool IsValid(this Instruction instruction, IReleaseSpec spec)
        {
            if (!FastEnum.IsDefined(instruction))
            {
                return false;
            }

            return instruction switch
            {
                Instruction.TLOAD or Instruction.TSTORE => spec.TransientStorageEnabled,
                Instruction.REVERT => spec.RevertOpcodeEnabled,
                Instruction.STATICCALL => spec.StaticCallEnabled,
                Instruction.CREATE2 => spec.Create2OpcodeEnabled,
                Instruction.DELEGATECALL => spec.DelegateCallEnabled,
                Instruction.PUSH0 => spec.IncludePush0Instruction,
                Instruction.BEGINSUB or Instruction.RETURNSUB or Instruction.JUMPSUB when spec.SubroutinesEnabled => true,
                Instruction.RJUMP or Instruction.RJUMPI when spec.StaticRelativeJumpsEnabled => true,
                Instruction.CALLF or Instruction.RETF when spec.FunctionSections => true,
                Instruction.BASEFEE => spec.BaseFeeEnabled,
                Instruction.SELFBALANCE => spec.SelfBalanceOpcodeEnabled,
                Instruction.CHAINID => spec.ChainIdOpcodeEnabled,
                Instruction.EXTCODEHASH => spec.ExtCodeHashOpcodeEnabled,
                Instruction.EXTCODECOPY or Instruction.EXTCODESIZE => spec.ReturnDataOpcodesEnabled,
                Instruction.SHL or Instruction.SHR or Instruction.SAR => spec.ShiftOpcodesEnabled,
                _ => true
            };
        }

        public static (int InputCount, int immediated, int OutputCount) StackRequirements(this Instruction instruction, IReleaseSpec spec) => instruction switch
        {
            Instruction.STOP or Instruction.POP or Instruction.JUMPDEST or Instruction.INVALID or Instruction.CALLF or Instruction.RETF => (0, instruction is Instruction.CALLF ? 2 : 0, 0),
            Instruction.ADD or Instruction.MUL or Instruction.SUB or Instruction.DIV or Instruction.SDIV or Instruction.MOD or Instruction.SMOD or
            Instruction.EXP or Instruction.SIGNEXTEND or Instruction.LT or Instruction.GT or Instruction.SLT or Instruction.SGT or Instruction.EQ or
            Instruction.AND or Instruction.OR or Instruction.XOR or Instruction.BYTE or Instruction.SHL or Instruction.SHR or Instruction.SAR or Instruction.SHA3 => (2, 0, 1),
            Instruction.ADDMOD or Instruction.MULMOD or Instruction.CALLDATACOPY or Instruction.CREATE => (3, 0, 1),
            Instruction.ISZERO or Instruction.NOT or Instruction.BALANCE or Instruction.EXTCODESIZE or Instruction.EXTCODEHASH or
            Instruction.BLOCKHASH or Instruction.SLOAD or Instruction.MLOAD or Instruction.TLOAD => (1, 0, 1),
            Instruction.ORIGIN or Instruction.ADDRESS or Instruction.CALLER or Instruction.CALLVALUE or
            Instruction.CALLDATALOAD or Instruction.CALLDATASIZE or Instruction.CODESIZE or Instruction.GASPRICE or
            Instruction.RETURNDATASIZE or Instruction.COINBASE or Instruction.TIMESTAMP or Instruction.NUMBER or
            Instruction.PREVRANDAO or Instruction.GASLIMIT or Instruction.CHAINID or Instruction.SELFBALANCE or
            Instruction.BASEFEE or Instruction.PC or Instruction.MSIZE or Instruction.GAS => (0, 0, 1),
            Instruction.MSTORE or Instruction.MSTORE8 or Instruction.SSTORE or Instruction.JUMPI or Instruction.RETURN or Instruction.REVERT or Instruction.TSTORE => (2, 0, 0),
            Instruction.CODECOPY or Instruction.RETURNDATACOPY => (3, 0, 0),
            Instruction.JUMP or Instruction.SELFDESTRUCT or Instruction.JUMPSUB => (1, 0, 0),
            Instruction.CALL or Instruction.CALLCODE => (7, 0, 1),
            Instruction.DELEGATECALL or Instruction.STATICCALL => (6, 0, 1),
            Instruction.EXTCODECOPY => (4, 0, 0),
            Instruction.CREATE2 => (4, 0, 1),

            Instruction.RJUMP when spec.IsEip4200Enabled || spec.SubroutinesEnabled => spec.IsEip4200Enabled ? (0, 2, 0) : (0, 0, 0),
            Instruction.RJUMPI when spec.IsEip4200Enabled || spec.SubroutinesEnabled => spec.IsEip4200Enabled ? (1, 2, 0) : (0, 0, 0),

            >= Instruction.LOG0 and <= Instruction.LOG4 => (2 + instruction - Instruction.LOG0, 0, 0),
            >= Instruction.PUSH0 and <= Instruction.PUSH32 => (0, instruction - Instruction.PUSH0, instruction - Instruction.PUSH0),
            >= Instruction.DUP1 and <= Instruction.DUP16 => (instruction - Instruction.DUP1 + 1, 0, instruction - Instruction.DUP1 + 2),
            >= Instruction.SWAP1 and <= Instruction.SWAP16 => (instruction - Instruction.SWAP1 + 2, 0, instruction - Instruction.SWAP1 + 2),
            _ => throw new NotImplementedException()
        };

        public static string? GetName(this Instruction instruction, bool isPostMerge = false, IReleaseSpec? spec = null)
        {
            spec ??= Frontier.Instance;
            return instruction switch
            {
                Instruction.PREVRANDAO => isPostMerge ? "PREVRANDAO" : "DIFFICULTY",
                Instruction.RJUMP => spec.StaticRelativeJumpsEnabled ? "RJUMP" : "BEGINSUB",
                Instruction.RJUMPI => spec.StaticRelativeJumpsEnabled ? "RJUMPI" : "RETURNSUB",
                _ => FastEnum.IsDefined(instruction) ? FastEnum.GetName(instruction) : null,
            };
        }

        public static bool IsOnlyForEofBytecode(this Instruction instruction) => instruction switch
        {
            Instruction.RJUMP or Instruction.RJUMPI or Instruction.CALLF or Instruction.RETF => true,
            _ => false
        };

        public static bool IsTerminatingInstruction(this Instruction instruction, IReleaseSpec spec = null) => instruction switch
        {
            Instruction.INVALID or Instruction.STOP or Instruction.RETURN or Instruction.REVERT => true,
            Instruction.RETF => spec?.IsEip4750Enabled ?? true,
            // Instruction.SELFDESTRUCT => true
            _ => false
        };
    }
}
