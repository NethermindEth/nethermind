// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm;

/// <summary>
/// Enumeration for specifying the type of storage access.
/// </summary>
public enum StorageAccessType
{
    /// <summary>
    /// Indicates a persistent storage read (SLOAD) operation.
    /// </summary>
    SLOAD,

    /// <summary>
    /// Indicates a persistent storage write (SSTORE) operation.
    /// </summary>
    SSTORE
}
