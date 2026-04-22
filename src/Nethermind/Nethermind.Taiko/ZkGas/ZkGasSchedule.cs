// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Taiko.ZkGas;

/// <summary>
/// Consensus-owned ZK gas schedule for the Uzen hardfork.
/// All values (multipliers, spawn estimates, and <see cref="BlockZkGasLimit"/>) are
/// taken verbatim from the canonical Taiko protocol specification:
/// <see href="https://github.com/taikoxyz/taiko-mono/blob/main/packages/protocol/docs/zk_gas_spec.md"/>.
/// The alethia-reth reference implementation lives in
/// <c>crates/evm/src/zk_gas/uzen.rs</c> in the alethia-reth repository.
/// Each opcode/precompile has a multiplier: zkGas = rawGas × multiplier.
/// </summary>
public static class ZkGasSchedule
{
    /// <summary>Maximum ZK gas permitted within a single Uzen block.</summary>
    public const ulong BlockZkGasLimit = 100_000_000;

    /// <summary>Fail-safe multiplier for any opcode or precompile not explicitly listed.</summary>
    public const ushort FailsafeMultiplier = ushort.MaxValue;

    /// <summary>Per-opcode proving-cost multipliers indexed by opcode byte.</summary>
    public static readonly ushort[] OpcodeMultipliers = BuildOpcodeMultipliers();

    /// <summary>Per-precompile proving-cost multipliers indexed by low-byte address.</summary>
    public static readonly ushort[] PrecompileMultipliers = BuildPrecompileMultipliers();

    // Fixed raw-gas estimates for spawn opcodes (used when the opcode actually opens a child frame).
    public const ulong SpawnEstimateCall = 12_500;
    public const ulong SpawnEstimateCallCode = 12_500;
    public const ulong SpawnEstimateDelegateCall = 3_500;
    public const ulong SpawnEstimateStaticCall = 3_500;
    public const ulong SpawnEstimateCreate = 37_000;
    public const ulong SpawnEstimateCreate2 = 44_500;

    private static ushort[] BuildOpcodeMultipliers()
    {
        ushort[] a = new ushort[256];
        Array.Fill(a, FailsafeMultiplier);

        a[0x09] = 152; // mulmod
        a[0x04] = 110; // div
        a[0x06] = 95;  // mod
        a[0x05] = 93;  // sdiv
        a[0x47] = 85;  // selfbalance
        a[0x20] = 85;  // keccak256
        a[0x08] = 71;  // addmod
        a[0x14] = 35;  // eq
        a[0x0a] = 33;  // exp
        a[0x07] = 29;  // smod
        a[0x1d] = 29;  // sar
        a[0x44] = 28;  // prevrandao
        a[0xf1] = 25;  // call
        a[0xf2] = 24;  // callcode
        a[0xfa] = 24;  // staticcall
        a[0x52] = 22;  // mstore
        a[0x30] = 22;  // address
        a[0x32] = 21;  // origin
        a[0x33] = 21;  // caller
        a[0x02] = 21;  // mul
        a[0xf4] = 21;  // delegatecall
        a[0x41] = 21;  // coinbase
        a[0x0b] = 21;  // signextend
        a[0x1b] = 20;  // shl
        a[0x35] = 20;  // calldataload
        a[0x51] = 20;  // mload
        a[0x93] = 19;  // swap4
        a[0x9c] = 19;  // swap13
        a[0x1c] = 19;  // shr
        a[0x9b] = 19;  // swap12
        a[0x9a] = 18;  // swap11
        a[0x92] = 18;  // swap3
        a[0x9d] = 18;  // swap14
        a[0x98] = 18;  // swap9
        a[0x91] = 18;  // swap2
        a[0x7e] = 18;  // push31
        a[0x9f] = 18;  // swap16
        a[0x9e] = 17;  // swap15
        a[0x7c] = 17;  // push29
        a[0x7b] = 17;  // push28
        a[0x96] = 17;  // swap7
        a[0x95] = 17;  // swap6
        a[0x99] = 17;  // swap10
        a[0x7f] = 17;  // push32
        a[0x90] = 16;  // swap1
        a[0x77] = 16;  // push24
        a[0x94] = 16;  // swap5
        a[0x75] = 16;  // push22
        a[0x97] = 15;  // swap8
        a[0x7a] = 15;  // push27
        a[0x4a] = 15;  // blobbasefee
        a[0x3a] = 14;  // gasprice
        a[0x79] = 14;  // push26
        a[0x12] = 14;  // slt
        a[0x74] = 14;  // push21
        a[0x13] = 14;  // sgt
        a[0x03] = 13;  // sub
        a[0x34] = 13;  // callvalue
        a[0x78] = 13;  // push25
        a[0x70] = 13;  // push17
        a[0x73] = 13;  // push20
        a[0x39] = 13;  // codecopy
        a[0x55] = 13;  // sstore
        a[0x6d] = 12;  // push14
        a[0x37] = 12;  // calldatacopy
        a[0x7d] = 12;  // push30
        a[0x76] = 12;  // push23
        a[0x58] = 12;  // pc
        a[0x01] = 12;  // add
        a[0x72] = 11;  // push19
        a[0x5a] = 11;  // gas
        a[0x42] = 11;  // timestamp
        a[0x48] = 11;  // basefee
        a[0x43] = 11;  // number
        a[0x71] = 11;  // push18
        a[0x36] = 11;  // calldatasize
        a[0x6f] = 11;  // push16
        a[0x38] = 11;  // codesize
        a[0x46] = 11;  // chainid
        a[0x10] = 11;  // lt
        a[0x45] = 11;  // gaslimit
        a[0x59] = 11;  // msize
        a[0x3d] = 10;  // returndatasize
        a[0x5f] = 10;  // push0
        a[0x6e] = 10;  // push15
        a[0x11] = 10;  // gt
        a[0x69] = 10;  // push10
        a[0x49] = 10;  // blobhash
        a[0x6b] = 9;   // push12
        a[0x68] = 9;   // push9
        a[0x17] = 9;   // or
        a[0x53] = 9;   // mstore8
        a[0x1a] = 9;   // byte
        a[0x18] = 9;   // xor
        a[0x5b] = 9;   // jumpdest
        a[0x3e] = 9;   // returndatacopy
        a[0x6a] = 8;   // push11
        a[0x16] = 8;   // and
        a[0x8b] = 8;   // dup12
        a[0x8e] = 8;   // dup15
        a[0x65] = 8;   // push6
        a[0x63] = 8;   // push4
        a[0x15] = 8;   // iszero
        a[0x3f] = 8;   // extcodehash
        a[0x6c] = 7;   // push13
        a[0x66] = 7;   // push7
        a[0x40] = 7;   // blockhash
        a[0x88] = 7;   // dup9
        a[0x67] = 7;   // push8
        a[0x8d] = 7;   // dup14
        a[0xa1] = 7;   // log1
        a[0xa0] = 6;   // log0
        a[0x19] = 6;   // not
        a[0x8f] = 6;   // dup16
        a[0x84] = 6;   // dup5
        a[0x62] = 6;   // push3
        a[0x85] = 6;   // dup6
        a[0x87] = 6;   // dup8
        a[0x3b] = 6;   // extcodesize
        a[0x31] = 6;   // balance
        a[0x80] = 6;   // dup1
        a[0x82] = 6;   // dup3
        a[0x5d] = 6;   // tstore
        a[0x8c] = 6;   // dup13
        a[0x8a] = 6;   // dup11
        a[0x3c] = 6;   // extcodecopy
        a[0x83] = 6;   // dup4
        a[0x64] = 6;   // push5
        a[0x50] = 5;   // pop
        a[0x54] = 5;   // sload
        a[0x5e] = 5;   // mcopy
        a[0x60] = 5;   // push1
        a[0x89] = 5;   // dup10
        a[0x61] = 5;   // push2
        a[0x86] = 5;   // dup7
        a[0xa3] = 5;   // log3
        a[0x81] = 5;   // dup2
        a[0xa4] = 5;   // log4
        a[0x57] = 5;   // jumpi
        a[0xa2] = 4;   // log2
        a[0x56] = 3;   // jump
        a[0x5c] = 1;   // tload
        a[0xf0] = 1;   // create
        a[0xf5] = 1;   // create2
        a[0x00] = 0;   // stop
        a[0xf3] = 0;   // return
        a[0xfd] = 0;   // revert
        a[0xff] = 0;   // selfdestruct
        a[0xfe] = 0;   // invalid

        return a;
    }

    private static ushort[] BuildPrecompileMultipliers()
    {
        ushort[] a = new ushort[256];
        Array.Fill(a, FailsafeMultiplier);

        a[0x05] = 1363; // modexp
        a[0x0a] = 398;  // point_evaluation
        a[0x09] = 243;  // blake2f
        a[0x12] = 159;  // bls12_map_fp_to_g1
        a[0x11] = 134;  // bls12_pairing
        a[0x0b] = 112;  // bls12_g1add
        a[0x13] = 112;  // bls12_map_fp2_to_g2
        a[0x0e] = 111;  // bls12_g2add
        a[0x07] = 87;   // bn128_mul
        a[0x08] = 82;   // bn128_pairing
        a[0x01] = 81;   // ecrecover
        a[0x0c] = 52;   // bls12_g1msm
        a[0x0f] = 39;   // bls12_g2msm
        a[0x06] = 38;   // bn128_add
        a[0x02] = 10;   // sha256
        a[0x03] = 3;    // ripemd160
        a[0x04] = 2;    // identity

        return a;
    }
}
