// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
}
