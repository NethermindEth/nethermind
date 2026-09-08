// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

/// <summary>
/// Represents the <see href="https://eips.ethereum.org/EIPS/eip-8304#specification">EIP-8304</see> parameters.
/// </summary>
public static class Eip8304Constants
{
    public const string ContractAddressKey = "INDEX_CONTRACT_ADDRESS";

    /// <summary>
    /// The <c>TABLE_SIZES</c> parameter — table sizes at each level of the hierarchy.
    /// </summary>
    public static readonly int[] TableSizes = [1, 4, 16, 64, 256];

    /// <summary>
    /// The number of level-<c>i-1</c> tables merged to form one level-<c>i</c> table.
    /// </summary>
    public const int SubTablesPerTable = 4;

    /// <summary>
    /// The <c>TABLES_PER_LEVEL</c> parameter — ring buffer size per table level.
    /// </summary>
    public const int TablesPerLevel = 1024;

    /// <summary>
    /// Gas limit for the system call to the index contract.
    /// </summary>
    public const ulong GasLimit = 30_000_000UL;

    /// <summary>
    /// System call calldata length: first_block (32B) + table_size (32B) + table_root (32B).
    /// </summary>
    public const int CalldataLength = 96;

    /// <summary>
    /// The minimum number of historical blocks required for sync recovery.
    /// Equal to <c>TABLE_SIZES[4] * 5 / 4 - 1 = 319</c> with current parameters.
    /// </summary>
    public static readonly int SyncRecoveryBlocks = TableSizes[^1] * 5 / 4 - 1;
}
