// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Taiko.ZkGas;

/// <summary>
/// Consensus-owned ZK gas schedule for the Unzen hardfork.
/// Each opcode/precompile has a multiplier: zkGas = rawGas × multiplier.
/// </summary>
/// <remarks>
/// The default tables ([<see cref="OpcodeMultipliers"/>], [<see cref="PrecompileMultipliers"/>])
/// hold the recalibrated multipliers from taiko-mono#21720 used by Devnet / Hoodi / Mainnet
/// when Unzen activates. The Masaya tables ([<see cref="MasayaOpcodeMultipliers"/>],
/// [<see cref="MasayaPrecompileMultipliers"/>]) keep the prior values frozen because Masaya
/// already finalized Unzen blocks under those multipliers — retroactive recalibration would
/// break consensus on its history, same rationale as <see cref="MasayaTxIntrinsicZkGas"/>.
/// Resolution by chain id lives in <see cref="OpcodeMultipliersFor(ulong)"/> /
/// <see cref="PrecompileMultipliersFor(ulong)"/>.
/// </remarks>
public static class ZkGasSchedule
{
    /// <summary>Chain id of the Taiko Alethia mainnet.</summary>
    public const ulong TaikoMainnetChainId = 167_000;

    /// <summary>Chain id of the Taiko devnet.</summary>
    public const ulong TaikoDevnetChainId = 167_001;

    /// <summary>Chain id of the Taiko Masaya testnet.</summary>
    public const ulong TaikoMasayaChainId = 167_011;

    /// <summary>Chain id of the Taiko Hoodi testnet.</summary>
    public const ulong TaikoHoodiChainId = 167_013;

    /// <summary>Default Unzen block ZK gas limit.</summary>
    public const ulong BlockZkGasLimit = 100_000_000;

    /// <summary>Masaya-specific block ZK gas limit (1B). Masaya activated Unzen with a larger budget for load testing.</summary>
    public const ulong MasayaBlockZkGasLimit = 1_000_000_000;

    /// <summary>Fixed ZK gas charged per transaction before any opcode runs; covers proving cost of sender ecrecovery.</summary>
    public const ulong TxIntrinsicZkGas = 243_000;

    /// <summary>
    /// Per-transaction intrinsic ZK gas for Masaya. Pinned at 0 because Masaya activated Unzen before
    /// this constant landed; retroactively charging would break consensus on finalized blocks.
    /// </summary>
    public const ulong MasayaTxIntrinsicZkGas = 0;

    /// <summary>
    /// Mainnet batch-lookup threshold: the first allowed block id (first Shasta block).
    /// Resolved batch lookup results <em>strictly less than</em> this value are reported
    /// to the driver as JSON null; the value itself passes through unchanged. Sourced
    /// from taiko-geth PR #558 and alethia-reth PR #177. Named for the comparison
    /// semantics rather than the upstream "last Pacaya" label, which is inverted
    /// relative to the strict-<c>&lt;</c> operator.
    /// </summary>
    public const ulong TaikoMainnetBatchLookupThreshold = 4_990_434;

    /// <summary>
    /// Hoodi batch-lookup threshold: the first allowed block id (first Shasta block).
    /// Resolved batch lookup results <em>strictly less than</em> this value are reported
    /// to the driver as JSON null; the value itself passes through unchanged. Sourced
    /// from taiko-geth PR #558 and alethia-reth PR #177.
    /// </summary>
    public const ulong TaikoHoodiBatchLookupThreshold = 3_951_005;

    /// <summary>
    /// Returns the per-network minimum block id for batch lookup RPC results, or
    /// <c>null</c> on networks with no threshold (Devnet, Masaya, unknown). When a
    /// threshold is configured, <c>taikoAuth_last{Certain,}{L1Origin,BlockID}ByBatchID</c>
    /// must report JSON null for any resolved block id strictly below it. Mirrors
    /// <c>batchLookupBlockThresholds</c> in taiko-geth (PR #558) and
    /// <c>batch_lookup_last_pacaya_block_threshold</c> in alethia-reth (PR #177).
    /// </summary>
    /// <param name="chainId">Chain id from <see cref="Nethermind.Core.Specs.ISpecProvider.ChainId"/>.</param>
    public static ulong? ResolveBatchLookupThreshold(ulong chainId) => chainId switch
    {
        TaikoMainnetChainId => TaikoMainnetBatchLookupThreshold,
        TaikoHoodiChainId => TaikoHoodiBatchLookupThreshold,
        _ => null,
    };

    /// <summary>Fail-safe multiplier for any opcode or precompile not explicitly listed.</summary>
    public const ushort FailsafeMultiplier = ushort.MaxValue;

    // Backing arrays are kept private so they cannot be mutated in-place by external code.
    // The public surface returns ReadOnlySpan<ushort> for index-based reads and
    // ReadOnlyMemory<ushort> from the chain-id resolvers so callers can stash a stable handle
    // without exposing the underlying array.
    private static readonly ushort[] _opcodeMultipliers = BuildOpcodeMultipliers();
    private static readonly ushort[] _precompileMultipliers = BuildPrecompileMultipliers();
    private static readonly ushort[] _masayaOpcodeMultipliers = BuildMasayaOpcodeMultipliers();
    private static readonly ushort[] _masayaPrecompileMultipliers = BuildMasayaPrecompileMultipliers();

    /// <summary>
    /// Default per-opcode proving-cost multipliers indexed by opcode byte. Used by Devnet,
    /// Hoodi, and Mainnet — i.e. every network except Masaya.
    /// </summary>
    public static ReadOnlySpan<ushort> OpcodeMultipliers => _opcodeMultipliers;

    /// <summary>
    /// Default per-precompile proving-cost multipliers indexed by low-byte address. Used by
    /// Devnet, Hoodi, and Mainnet — i.e. every network except Masaya.
    /// </summary>
    public static ReadOnlySpan<ushort> PrecompileMultipliers => _precompileMultipliers;

    /// <summary>
    /// Frozen Masaya per-opcode multipliers, pinned at the pre-recalibration values to preserve
    /// consensus on its already-finalized Unzen blocks.
    /// </summary>
    public static ReadOnlySpan<ushort> MasayaOpcodeMultipliers => _masayaOpcodeMultipliers;

    /// <summary>
    /// Frozen Masaya per-precompile multipliers, pinned at the pre-recalibration values for
    /// the same consensus reason as <see cref="MasayaOpcodeMultipliers"/>.
    /// </summary>
    public static ReadOnlySpan<ushort> MasayaPrecompileMultipliers => _masayaPrecompileMultipliers;

    /// <summary>
    /// Returns the per-opcode multiplier table for the given chain id. Masaya gets the frozen
    /// pre-recalibration table; every other network gets the recalibrated default.
    /// </summary>
    /// <param name="chainId">Chain id from <see cref="Nethermind.Core.Specs.ISpecProvider.ChainId"/>.</param>
    /// <remarks>
    /// Returns <see cref="ReadOnlyMemory{T}"/> rather than the raw array so callers that stash
    /// the handle in a field cannot mutate the shared singleton table; index via
    /// <c>.Span[opcode]</c>.
    /// </remarks>
    public static ReadOnlyMemory<ushort> OpcodeMultipliersFor(ulong chainId) =>
        chainId == TaikoMasayaChainId ? _masayaOpcodeMultipliers : _opcodeMultipliers;

    /// <summary>
    /// Returns the per-precompile multiplier table for the given chain id. Masaya gets the
    /// frozen pre-recalibration table; every other network gets the recalibrated default.
    /// </summary>
    /// <param name="chainId">Chain id from <see cref="Nethermind.Core.Specs.ISpecProvider.ChainId"/>.</param>
    /// <remarks>
    /// Returns <see cref="ReadOnlyMemory{T}"/> rather than the raw array so callers that stash
    /// the handle in a field cannot mutate the shared singleton table; index via
    /// <c>.Span[addressLowByte]</c>.
    /// </remarks>
    public static ReadOnlyMemory<ushort> PrecompileMultipliersFor(ulong chainId) =>
        chainId == TaikoMasayaChainId ? _masayaPrecompileMultipliers : _precompileMultipliers;

    // Fixed raw-gas estimates for spawn opcodes (used when the opcode actually opens a child frame).
    public const ulong SpawnEstimateCall = 12_500;
    public const ulong SpawnEstimateCallCode = 12_500;
    public const ulong SpawnEstimateDelegateCall = 3_500;
    public const ulong SpawnEstimateStaticCall = 3_500;
    public const ulong SpawnEstimateCreate = 37_000;
    public const ulong SpawnEstimateCreate2 = 44_500;

    private static ushort[] BuildMasayaOpcodeMultipliers()
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

    private static ushort[] BuildMasayaPrecompileMultipliers()
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

    // Recalibrated opcode multipliers from taiko-mono#21720 — SP1 v6.1.0 + risc0 v3.0.5,
    // multiplier = max(SP1_µs_per_gas, risc0_µs_per_gas / 6). Adopted by alethia-reth#187
    // for the default Unzen schedule (Devnet / Hoodi / Mainnet).
    private static ushort[] BuildOpcodeMultipliers()
    {
        ushort[] a = new ushort[256];
        Array.Fill(a, FailsafeMultiplier);

        a[0x00] = 0;   // stop
        a[0x01] = 19;  // add
        a[0x02] = 19;  // mul
        a[0x03] = 22;  // sub
        a[0x04] = 76;  // div
        a[0x05] = 78;  // sdiv
        a[0x06] = 66;  // mod
        a[0x07] = 28;  // smod
        a[0x08] = 52;  // addmod
        a[0x09] = 113; // mulmod
        a[0x0a] = 21;  // exp
        a[0x0b] = 17;  // signextend
        a[0x10] = 19;  // lt
        a[0x11] = 19;  // gt
        a[0x12] = 20;  // slt
        a[0x13] = 19;  // sgt
        a[0x14] = 36;  // eq
        a[0x15] = 16;  // iszero
        a[0x16] = 19;  // and
        a[0x17] = 20;  // or
        a[0x18] = 18;  // xor
        a[0x19] = 15;  // not
        a[0x1a] = 17;  // byte
        a[0x1b] = 24;  // shl
        a[0x1c] = 22;  // shr
        a[0x1d] = 21;  // sar
        a[0x20] = 31;  // keccak256
        a[0x30] = 19;  // address
        a[0x31] = 4;   // balance
        a[0x32] = 21;  // origin
        a[0x33] = 18;  // caller
        a[0x34] = 11;  // callvalue
        a[0x35] = 22;  // calldataload
        a[0x36] = 13;  // calldatasize
        a[0x37] = 13;  // calldatacopy
        a[0x38] = 11;  // codesize
        a[0x39] = 12;  // codecopy
        a[0x3a] = 15;  // gasprice
        a[0x3b] = 4;   // extcodesize
        a[0x3c] = 4;   // extcodecopy
        a[0x3d] = 12;  // returndatasize
        a[0x3e] = 10;  // returndatacopy
        a[0x3f] = 7;   // extcodehash
        a[0x40] = 6;   // blockhash
        a[0x41] = 18;  // coinbase
        a[0x42] = 10;  // timestamp
        a[0x43] = 12;  // number
        a[0x44] = 42;  // prevrandao
        a[0x45] = 13;  // gaslimit
        a[0x46] = 11;  // chainid
        a[0x47] = 52;  // selfbalance
        a[0x48] = 14;  // basefee
        a[0x49] = 13;  // blobhash
        a[0x4a] = 15;  // blobbasefee
        a[0x50] = 10;  // pop
        a[0x51] = 18;  // mload
        a[0x52] = 29;  // mstore
        a[0x53] = 10;  // mstore8
        a[0x54] = 3;   // sload
        a[0x55] = 5;   // sstore
        a[0x56] = 4;   // jump
        a[0x57] = 5;   // jumpi
        a[0x58] = 13;  // pc
        a[0x59] = 13;  // msize
        a[0x5a] = 11;  // gas
        a[0x5b] = 20;  // jumpdest
        a[0x5c] = 1;   // tload
        a[0x5d] = 5;   // tstore
        a[0x5e] = 4;   // mcopy
        a[0x5f] = 13;  // push0
        a[0x60] = 9;   // push1
        a[0x61] = 8;   // push2
        a[0x62] = 9;   // push3
        a[0x63] = 10;  // push4
        a[0x64] = 9;   // push5
        a[0x65] = 12;  // push6
        a[0x66] = 10;  // push7
        a[0x67] = 15;  // push8
        a[0x68] = 13;  // push9
        a[0x69] = 12;  // push10
        a[0x6a] = 12;  // push11
        a[0x6b] = 15;  // push12
        a[0x6c] = 15;  // push13
        a[0x6d] = 17;  // push14
        a[0x6e] = 21;  // push15
        a[0x6f] = 13;  // push16
        a[0x70] = 20;  // push17
        a[0x71] = 18;  // push18
        a[0x72] = 20;  // push19
        a[0x73] = 20;  // push20
        a[0x74] = 18;  // push21
        a[0x75] = 14;  // push22
        a[0x76] = 22;  // push23
        a[0x77] = 24;  // push24
        a[0x78] = 22;  // push25
        a[0x79] = 24;  // push26
        a[0x7a] = 16;  // push27
        a[0x7b] = 17;  // push28
        a[0x7c] = 28;  // push29
        a[0x7d] = 29;  // push30
        a[0x7e] = 16;  // push31
        a[0x7f] = 19;  // push32
        a[0x80] = 10;  // dup1
        a[0x81] = 8;   // dup2
        a[0x82] = 9;   // dup3
        a[0x83] = 10;  // dup4
        a[0x84] = 11;  // dup5
        a[0x85] = 8;   // dup6
        a[0x86] = 8;   // dup7
        a[0x87] = 10;  // dup8
        a[0x88] = 9;   // dup9
        a[0x89] = 10;  // dup10
        a[0x8a] = 10;  // dup11
        a[0x8b] = 9;   // dup12
        a[0x8c] = 9;   // dup13
        a[0x8d] = 8;   // dup14
        a[0x8e] = 8;   // dup15
        a[0x8f] = 10;  // dup16
        a[0x90] = 31;  // swap1
        a[0x91] = 30;  // swap2
        a[0x92] = 32;  // swap3
        a[0x93] = 31;  // swap4
        a[0x94] = 33;  // swap5
        a[0x95] = 34;  // swap6
        a[0x96] = 31;  // swap7
        a[0x97] = 30;  // swap8
        a[0x98] = 32;  // swap9
        a[0x99] = 31;  // swap10
        a[0x9a] = 33;  // swap11
        a[0x9b] = 32;  // swap12
        a[0x9c] = 31;  // swap13
        a[0x9d] = 31;  // swap14
        a[0x9e] = 36;  // swap15
        a[0x9f] = 31;  // swap16
        a[0xa0] = 3;   // log0
        a[0xa1] = 3;   // log1
        a[0xa2] = 2;   // log2
        a[0xa3] = 2;   // log3
        a[0xa4] = 2;   // log4
        a[0xf0] = 1;   // create
        a[0xf1] = 20;  // call
        a[0xf2] = 20;  // callcode
        a[0xf3] = 0;   // return
        a[0xf4] = 17;  // delegatecall
        a[0xf5] = 1;   // create2
        a[0xfa] = 23;  // staticcall
        a[0xfd] = 0;   // revert
        a[0xfe] = 0;   // invalid
        a[0xff] = 0;   // selfdestruct

        return a;
    }

    // Recalibrated precompile multipliers from taiko-mono#21720. BLS12-381 entries follow
    // the address scheme defined in packages/protocol/docs/zk_gas_spec.md (Appendix B), which
    // assigns G2Add=0x0e, G2Msm=0x0f, Pairing=0x11, MapFpToG1=0x12, MapFp2ToG2=0x13 — i.e. the
    // spec leaves 0x0d and 0x10 unset. The same indices appear in alethia-reth (crates/evm/src/
    // zk_gas/unzen.rs) and taiko-geth, so all clients tabulate against the same byte. Whether
    // the underlying EVM precompile registration uses canonical EIP-2537 addresses or the
    // spec's offset scheme is an orthogonal question — out of scope for this recalibration PR.
    private static ushort[] BuildPrecompileMultipliers()
    {
        ushort[] a = new ushort[256];
        Array.Fill(a, FailsafeMultiplier);

        a[0x01] = 47;  // ecrecover
        a[0x02] = 10;  // sha256
        a[0x03] = 4;   // ripemd160
        a[0x04] = 6;   // identity
        a[0x05] = 923; // modexp
        a[0x06] = 19;  // bn128_add
        a[0x07] = 58;  // bn128_mul
        a[0x08] = 54;  // bn128_pairing
        a[0x09] = 166; // blake2f
        a[0x0a] = 859; // point_evaluation
        a[0x0b] = 201; // bls12_g1add
        a[0x0c] = 93;  // bls12_g1msm
        a[0x0e] = 230; // bls12_g2add
        a[0x0f] = 71;  // bls12_g2msm
        a[0x11] = 365; // bls12_pairing
        a[0x12] = 246; // bls12_map_fp_to_g1
        a[0x13] = 208; // bls12_map_fp2_to_g2

        return a;
    }
}
