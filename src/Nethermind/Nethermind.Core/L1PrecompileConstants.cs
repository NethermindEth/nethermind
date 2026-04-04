// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core;

/// <summary>
/// Shared constants for L1SLOAD/L1CALL precompile operations.
/// </summary>
public static class L1PrecompileConstants
{
    /// <summary>
    /// Number of bytes for the L1 contract address in the input
    /// </summary>
    public const int AddressBytes = 20;

    /// <summary>
    /// Number of bytes for the storage key in the input
    /// </summary>
    public const int StorageKeyBytes = 32;

    /// <summary>
    /// Number of bytes for the block number in the input
    /// </summary>
    public const int BlockNumberBytes = 32;

    /// <summary>
    /// Total expected input length for L1SLOAD precompile calls
    /// </summary>
    public const int ExpectedInputLength = AddressBytes + StorageKeyBytes + BlockNumberBytes;

    /// <summary>
    /// Fixed gas cost for L1SLOAD precompile calls
    /// </summary>
    public const long FixedGasCost = 2000L;

    /// <summary>
    /// Per-load gas cost for L1SLOAD precompile calls
    /// </summary>
    public const long PerLoadGasCost = 2000L;

    // --- L1STATICCALL constants (Surge precompile spec) ---

    /// <summary>
    /// Minimum input length for L1STATICCALL: address(20) + blockNumber(32) = 52 bytes
    /// </summary>
    public const int L1StaticCallMinInputLength = AddressBytes + BlockNumberBytes;

    /// <summary>
    /// Fixed base gas cost for L1STATICCALL precompile calls.
    /// Covers address parsing and validation overhead.
    /// </summary>
    public const long L1StaticCallFixedGasCost = 2000L;

    /// <summary>
    /// Per-call overhead gas cost for L1STATICCALL (full call execution cost).
    /// Covers the L1 RPC round-trip and call execution.
    /// </summary>
    public const long L1StaticCallPerCallOverhead = 10000L;

    /// <summary>
    /// Per-byte calldata gas cost for L1STATICCALL.
    /// Matches EVM CALLDATACOPY cost (16 gas/byte) per EIP-2028.
    /// </summary>
    public const long L1StaticCallPerByteCalldataCost = 16L;

    /// <summary>
    /// Maximum return data size for L1STATICCALL (24 KB).
    /// Matches MAX_CODE_SIZE (EIP-170) to bound memory usage.
    /// </summary>
    public const int L1StaticCallMaxReturnDataSize = 24576;

    // --- Shared L1 RPC constants ---

    /// <summary>
    /// Timeout for L1 RPC calls (eth_call, eth_getStorageAt).
    /// Shared by all L1 precompile providers to ensure consistent behavior.
    /// </summary>
    public static readonly TimeSpan L1RpcTimeout = TimeSpan.FromSeconds(30);
}
