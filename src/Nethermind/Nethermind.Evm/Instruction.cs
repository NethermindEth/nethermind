// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using FastEnumUtility;
using Nethermind.Core.Specs;

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

        // EIP-1153
        TLOAD = 0x5c,
        TSTORE = 0x5d,

        CREATE = 0xf0,
        CALL = 0xf1,
        CALLCODE = 0xf2,
        RETURN = 0xf3,
        DELEGATECALL = 0xf4,
        CREATE2 = 0xf5,
        STATICCALL = 0xfa,
        REVERT = 0xfd,
        INVALID = 0xfe,
        SELFDESTRUCT = 0xff,
    }
    public record struct OpcodeMetadata(long gasCost, byte additionalBytes, byte stackBehaviorPop, byte stackBehaviorPush)
    {
        /// <summary>
        /// The gas cost.
        /// </summary>
        public long GasCost { get; } = gasCost;

        /// <summary>
        /// How many following bytes does this instruction have.
        /// </summary>
        public byte AdditionalBytes { get; } = additionalBytes;

        /// <summary>
        /// How many bytes are popped by this instruction.
        /// </summary>
        public byte StackBehaviorPop { get; } = stackBehaviorPop;

        /// <summary>
        /// How many bytes are pushed by this instruction.
        /// </summary>
        public byte StackBehaviorPush { get; } = stackBehaviorPush;

        public static readonly IReadOnlyDictionary<Instruction, OpcodeMetadata> Operations =
            new Dictionary<Instruction, OpcodeMetadata>()
            {
                [Instruction.POP] = new(GasCostOf.Base, 0, 1, 0),
                [Instruction.PC] = new(GasCostOf.Base, 0, 0, 1),

                [Instruction.PUSH1] = new(GasCostOf.VeryLow, 1, 0, 1),
                [Instruction.PUSH2] = new(GasCostOf.VeryLow, 2, 0, 1),
                [Instruction.PUSH3] = new(GasCostOf.VeryLow, 3, 0, 1),
                [Instruction.PUSH4] = new(GasCostOf.VeryLow, 4, 0, 1),
                [Instruction.PUSH5] = new(GasCostOf.VeryLow, 5, 0, 1),
                [Instruction.PUSH6] = new(GasCostOf.VeryLow, 6, 0, 1),
                [Instruction.PUSH7] = new(GasCostOf.VeryLow, 7, 0, 1),
                [Instruction.PUSH8] = new(GasCostOf.VeryLow, 8, 0, 1),
                [Instruction.PUSH9] = new(GasCostOf.VeryLow, 9, 0, 1),
                [Instruction.PUSH10] = new(GasCostOf.VeryLow, 10, 0, 1),
                [Instruction.PUSH11] = new(GasCostOf.VeryLow, 11, 0, 1),
                [Instruction.PUSH12] = new(GasCostOf.VeryLow, 12, 0, 1),
                [Instruction.PUSH13] = new(GasCostOf.VeryLow, 13, 0, 1),
                [Instruction.PUSH14] = new(GasCostOf.VeryLow, 14, 0, 1),
                [Instruction.PUSH15] = new(GasCostOf.VeryLow, 15, 0, 1),
                [Instruction.PUSH16] = new(GasCostOf.VeryLow, 16, 0, 1),
                [Instruction.PUSH17] = new(GasCostOf.VeryLow, 17, 0, 1),
                [Instruction.PUSH18] = new(GasCostOf.VeryLow, 18, 0, 1),
                [Instruction.PUSH19] = new(GasCostOf.VeryLow, 19, 0, 1),
                [Instruction.PUSH20] = new(GasCostOf.VeryLow, 20, 0, 1),
                [Instruction.PUSH21] = new(GasCostOf.VeryLow, 21, 0, 1),
                [Instruction.PUSH22] = new(GasCostOf.VeryLow, 22, 0, 1),
                [Instruction.PUSH23] = new(GasCostOf.VeryLow, 23, 0, 1),
                [Instruction.PUSH24] = new(GasCostOf.VeryLow, 24, 0, 1),
                [Instruction.PUSH25] = new(GasCostOf.VeryLow, 25, 0, 1),
                [Instruction.PUSH26] = new(GasCostOf.VeryLow, 26, 0, 1),
                [Instruction.PUSH27] = new(GasCostOf.VeryLow, 27, 0, 1),
                [Instruction.PUSH28] = new(GasCostOf.VeryLow, 28, 0, 1),
                [Instruction.PUSH29] = new(GasCostOf.VeryLow, 29, 0, 1),
                [Instruction.PUSH30] = new(GasCostOf.VeryLow, 30, 0, 1),
                [Instruction.PUSH31] = new(GasCostOf.VeryLow, 31, 0, 1),
                [Instruction.PUSH32] = new(GasCostOf.VeryLow, 32, 0, 1),

                [Instruction.JUMPDEST] = new(GasCostOf.JumpDest, 0, 0, 0),
                [Instruction.JUMP] = new(GasCostOf.Mid, 0, 1, 0),
                [Instruction.JUMPI] = new(GasCostOf.High, 0, 2, 0),
                [Instruction.SUB] = new(GasCostOf.VeryLow, 0, 2, 1),

                [Instruction.DUP1] = new(GasCostOf.VeryLow, 0, 1, 2),
                [Instruction.DUP2] = new(GasCostOf.VeryLow, 0, 2, 3),
                [Instruction.DUP3] = new(GasCostOf.VeryLow, 0, 3, 4),
                [Instruction.DUP4] = new(GasCostOf.VeryLow, 0, 4, 5),
                [Instruction.DUP5] = new(GasCostOf.VeryLow, 0, 5, 6),
                [Instruction.DUP6] = new(GasCostOf.VeryLow, 0, 6, 7),
                [Instruction.DUP7] = new(GasCostOf.VeryLow, 0, 7, 8),
                [Instruction.DUP8] = new(GasCostOf.VeryLow, 0, 8, 9),
                [Instruction.DUP9] = new(GasCostOf.VeryLow, 0, 9, 10),
                [Instruction.DUP10] = new(GasCostOf.VeryLow, 0, 10, 11),
                [Instruction.DUP11] = new(GasCostOf.VeryLow, 0, 11, 12),
                [Instruction.DUP12] = new(GasCostOf.VeryLow, 0, 12, 13),
                [Instruction.DUP13] = new(GasCostOf.VeryLow, 0, 13, 14),
                [Instruction.DUP14] = new(GasCostOf.VeryLow, 0, 14, 15),
                [Instruction.DUP15] = new(GasCostOf.VeryLow, 0, 15, 16),
                [Instruction.DUP16] = new(GasCostOf.VeryLow, 0, 16, 17),

                [Instruction.SWAP1] = new(GasCostOf.VeryLow, 0, 2, 2),
                [Instruction.SWAP2] = new(GasCostOf.VeryLow, 0, 2, 2),
                [Instruction.SWAP3] = new(GasCostOf.VeryLow, 0, 2, 2),
                [Instruction.SWAP4] = new(GasCostOf.VeryLow, 0, 2, 2),
                [Instruction.SWAP5] = new(GasCostOf.VeryLow, 0, 2, 2),
                [Instruction.SWAP6] = new(GasCostOf.VeryLow, 0, 2, 2),
                [Instruction.SWAP7] = new(GasCostOf.VeryLow, 0, 2, 2),
                [Instruction.SWAP8] = new(GasCostOf.VeryLow, 0, 2, 2),
                [Instruction.SWAP9] = new(GasCostOf.VeryLow, 0, 2, 2),
                [Instruction.SWAP10] = new(GasCostOf.VeryLow, 0, 2, 2),
                [Instruction.SWAP11] = new(GasCostOf.VeryLow, 0, 2, 2),
                [Instruction.SWAP12] = new(GasCostOf.VeryLow, 0, 2, 2),
                [Instruction.SWAP13] = new(GasCostOf.VeryLow, 0, 2, 2),
                [Instruction.SWAP14] = new(GasCostOf.VeryLow, 0, 2, 2),
                [Instruction.SWAP15] = new(GasCostOf.VeryLow, 0, 2, 2),
                [Instruction.SWAP16] = new(GasCostOf.VeryLow, 0, 2, 2),

                [Instruction.ADD] = new(GasCostOf.VeryLow, 0, 2, 1),
                [Instruction.MUL] = new(GasCostOf.Low, 0, 2, 1),
                [Instruction.SUB] = new(GasCostOf.VeryLow, 0, 2, 1),
                [Instruction.DIV] = new(GasCostOf.Low, 0, 2, 1),
                [Instruction.SDIV] = new(GasCostOf.Low, 0, 2, 1),
                [Instruction.MOD] = new(GasCostOf.Low, 0, 2, 1),
                [Instruction.SMOD] = new(GasCostOf.Low, 0, 2, 1),
                [Instruction.EXP] = new(GasCostOf.Exp, 0, 2, 1),
                [Instruction.SIGNEXTEND] = new(GasCostOf.Low, 0, 2, 1),
                [Instruction.LT] = new(GasCostOf.VeryLow, 0, 2, 1),
                [Instruction.GT] = new(GasCostOf.VeryLow, 0, 2, 1),
                [Instruction.SLT] = new(GasCostOf.VeryLow, 0, 2, 1),
                [Instruction.SGT] = new(GasCostOf.VeryLow, 0, 2, 1),
                [Instruction.EQ] = new(GasCostOf.VeryLow, 0, 2, 1),
                [Instruction.ISZERO] = new(GasCostOf.VeryLow, 0, 1, 1),
                [Instruction.AND] = new(GasCostOf.VeryLow, 0, 2, 1),
                [Instruction.OR] = new(GasCostOf.VeryLow, 0, 2, 1),
                [Instruction.XOR] = new(GasCostOf.VeryLow, 0, 2, 1),
                [Instruction.NOT] = new(GasCostOf.VeryLow, 0, 1, 1),
                [Instruction.ADDMOD] = new(GasCostOf.Mid, 0, 3, 1),
                [Instruction.MULMOD] = new(GasCostOf.Mid, 0, 3, 1),
                [Instruction.SHL] = new(GasCostOf.VeryLow, 0, 2, 1),
                [Instruction.SHR] = new(GasCostOf.VeryLow, 0, 2, 1),
                [Instruction.SAR] = new(GasCostOf.VeryLow, 0, 2, 1),
                [Instruction.BYTE] = new(GasCostOf.VeryLow, 0, 2, 1),

                [Instruction.KECCAK256] = new(GasCostOf.VeryLow, 0, 2, 1),
                [Instruction.ADDRESS] = new(GasCostOf.Base, 0, 0, 1),
                [Instruction.BALANCE] = new(GasCostOf.Balance, 0, 1, 1),
                [Instruction.ORIGIN] = new(GasCostOf.Base, 0, 0, 1),
                [Instruction.CALLER] = new(GasCostOf.Base, 0, 0, 1),
                [Instruction.CALLVALUE] = new(GasCostOf.Base, 0, 0, 1),
                [Instruction.CALLDATALOAD] = new(GasCostOf.VeryLow, 0, 1, 1),
                [Instruction.CALLDATASIZE] = new(GasCostOf.Base, 0, 0, 1),
                [Instruction.CALLDATACOPY] = new(GasCostOf.VeryLow, 0, 3, 0),
                [Instruction.CODESIZE] = new(GasCostOf.Base, 0, 0, 1),
                [Instruction.CODECOPY] = new(GasCostOf.VeryLow, 0, 3, 0),
                [Instruction.GASPRICE] = new(GasCostOf.Base, 0, 0, 1),
                [Instruction.EXTCODESIZE] = new(GasCostOf.ExtCode, 0, 1, 1),
                [Instruction.EXTCODECOPY] = new(GasCostOf.ExtCode, 0, 4, 0),
                [Instruction.RETURNDATASIZE] = new(GasCostOf.Base, 0, 0, 1),
                [Instruction.RETURNDATACOPY] = new(GasCostOf.VeryLow, 0, 3, 0),
                [Instruction.EXTCODEHASH] = new(GasCostOf.ExtCodeHash, 0, 1, 1),
                [Instruction.EXTCODECOPY] = new(GasCostOf.ExtCode, 0, 4, 0),

                [Instruction.BLOCKHASH] = new(GasCostOf.BlockHash, 0, 1, 1),
                [Instruction.COINBASE] = new(GasCostOf.Base, 0, 0, 1),
                [Instruction.TIMESTAMP] = new(GasCostOf.Base, 0, 0, 1),
                [Instruction.NUMBER] = new(GasCostOf.Base, 0, 0, 1),
                [Instruction.PREVRANDAO] = new(GasCostOf.Base, 0, 0, 1),
                [Instruction.GASLIMIT] = new(GasCostOf.Base, 0, 0, 1),
                [Instruction.CHAINID] = new(GasCostOf.Base, 0, 0, 1),
                [Instruction.SELFBALANCE] = new(GasCostOf.SelfBalance, 0, 0, 1),
                [Instruction.BASEFEE] = new(GasCostOf.Base, 0, 0, 1),
                [Instruction.BLOBHASH] = new(GasCostOf.BlobHash, 0, 1, 1),
                [Instruction.BLOBBASEFEE] = new(GasCostOf.Base, 0, 0, 1),

                [Instruction.POP] = new(GasCostOf.Base, 0, 1, 0),
                [Instruction.MLOAD] = new(GasCostOf.VeryLow, 0, 1, 1),
                [Instruction.MSTORE] = new(GasCostOf.VeryLow, 0, 2, 0),
                [Instruction.MSTORE8] = new(GasCostOf.VeryLow, 0, 2, 0),
                [Instruction.SLOAD] = new(GasCostOf.SLoad, 0, 1, 1),
                [Instruction.SSTORE] = new(GasCostOf.SSet, 0, 2, 0),
                [Instruction.JUMP] = new(GasCostOf.Mid, 0, 1, 0),
                [Instruction.JUMPI] = new(GasCostOf.Mid, 0, 2, 0),
                [Instruction.PC] = new(GasCostOf.Base, 0, 0, 1),
                [Instruction.MSIZE] = new(GasCostOf.Base, 0, 0, 1),
                [Instruction.GAS] = new(GasCostOf.Base, 0, 0, 1),
                [Instruction.JUMPDEST] = new(GasCostOf.JumpDest, 0, 0, 0),
                [Instruction.MCOPY] = new(GasCostOf.VeryLow, 0, 3, 0),

                [Instruction.LOG0] = new(GasCostOf.Log, 0, 2, 0),
                [Instruction.LOG1] = new(GasCostOf.Log, 0, 3, 0),
                [Instruction.LOG2] = new(GasCostOf.Log, 0, 4, 0),
                [Instruction.LOG3] = new(GasCostOf.Log, 0, 5, 0),
                [Instruction.LOG4] = new(GasCostOf.Log, 0, 6, 0),

                [Instruction.TLOAD] = new(GasCostOf.Base, 0, 1, 1),
                [Instruction.TSTORE] = new(GasCostOf.Base, 0, 2, 0),

                [Instruction.CREATE] = new(GasCostOf.Create, 0, 3, 1),
                [Instruction.CALL] = new(GasCostOf.Call, 0, 7, 1),
                [Instruction.CALLCODE] = new(GasCostOf.Call, 0, 7, 1),
                [Instruction.RETURN] = new(GasCostOf.Base, 0, 2, 0),
                [Instruction.DELEGATECALL] = new(GasCostOf.Call, 0, 6, 1),
                [Instruction.CREATE2] = new(GasCostOf.Create, 0, 4, 1),
                [Instruction.STATICCALL] = new(GasCostOf.Call, 0, 6, 1),
                [Instruction.REVERT] = new(GasCostOf.Base, 0, 2, 0),
                [Instruction.INVALID] = new(GasCostOf.Base, 0, 0, 0),
                [Instruction.SELFDESTRUCT] = new(GasCostOf.SelfDestruct, 0, 1, 0),
            }.ToFrozenDictionary();
    }
    public struct OpcodeInfo(ushort pc, Instruction instruction, int? argumentIndex)
    {
        public OpcodeMetadata? Metadata => OpcodeMetadata.Operations[instruction];
        public Instruction Operation => instruction;
        public ushort ProgramCounter => pc;
        public int? Arguments { get; set; } = argumentIndex;
    }

    public static class InstructionExtensions
    {
        public static bool IsStateful(this Instruction instruction) => instruction switch
        {
            Instruction.CREATE or Instruction.CREATE2 => true,
            Instruction.CALL or Instruction.CALLCODE or Instruction.DELEGATECALL or Instruction.STATICCALL => true,
            Instruction.SLOAD or Instruction.SSTORE => true,
            Instruction.TLOAD or Instruction.TSTORE => true,
            Instruction.EXTCODESIZE or Instruction.EXTCODECOPY or Instruction.EXTCODEHASH => true,
            Instruction.SELFDESTRUCT => true,
            Instruction.BALANCE => true,
            Instruction.SELFBALANCE => true,
            _ => false,
        };

        public static string? GetName(this Instruction instruction, bool isPostMerge = false, IReleaseSpec? spec = null) =>
            instruction switch
            {
                Instruction.PREVRANDAO when !isPostMerge => "DIFFICULTY",
                _ => FastEnum.IsDefined(instruction) ? FastEnum.GetName(instruction) : null
            };
    }
}
