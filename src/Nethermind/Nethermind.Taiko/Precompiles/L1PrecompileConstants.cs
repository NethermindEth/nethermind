// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Taiko.Precompiles;

/// <summary>
/// Shared constants for L1SLOAD and L1STATICCALL precompile operations.
/// </summary>
public static class L1PrecompileConstants
{
    // --- Shared constants ---

    public const int BlockNumberBytes = 32;

    /// <summary>
    /// Timeout for L1 RPC calls (eth_call, eth_getStorageAt).
    /// </summary>
    public static readonly TimeSpan L1RpcTimeout = TimeSpan.FromSeconds(30);

    // --- L1SLOAD constants (RIP-7728) ---

    public const int L1SloadStorageKeyBytes = 32;
    public const int L1SloadExpectedInputLength = Address.Size + L1SloadStorageKeyBytes + BlockNumberBytes;
    public const long L1SloadFixedGasCost = 2000L;
    public const long L1SloadPerLoadGasCost = 2000L;

    // --- L1STATICCALL constants (Surge precompile spec) ---

    public const int L1StaticCallMinInputLength = Address.Size + BlockNumberBytes;
    public const long L1StaticCallFixedGasCost = 2000L;
    /// <summary>
    /// Per-call overhead covering L1 RPC round-trip and call execution.
    /// </summary>
    public const long L1StaticCallPerCallOverhead = 10000L;
    /// <summary>
    /// Per-byte calldata cost matching EVM CALLDATACOPY (16 gas/byte, EIP-2028).
    /// </summary>
    public const long L1StaticCallPerByteCalldataCost = 16L;
    /// <summary>
    /// Maximum return data size (24 KB, matches MAX_CODE_SIZE per EIP-170).
    /// </summary>
    public const int L1StaticCallMaxReturnDataSize = 24576;

    /// <summary>
    /// Maximum gas limit passed to L1 debug_traceCall.
    /// The actual limit is min(remainingL2Gas, this cap).
    /// </summary>
    public const long L1CallMaxGasCap = 30_000_000L;
}
