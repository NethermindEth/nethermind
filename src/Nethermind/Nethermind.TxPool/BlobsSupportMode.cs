// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.TxPool;

/// <summary>
/// Defines blobs support mode.
/// </summary>
public enum BlobsSupportMode
{
    /// <summary>
    /// No support for blob transactions.
    /// </summary>
    Disabled,

    /// <summary>
    /// Blob transactions stored only in memory
    /// </summary>
    InMemory,

    /// <summary>
    /// Blob transactions stored in db.
    /// </summary>
    Storage,

    /// <summary>
    /// Blob transactions stored in db with support for restoring reorganized blob transactions to blob pool.
    /// </summary>
    StorageWithReorgs
}

public static class BlobsSupportModeExtensions
{
    public static bool IsPersistentStorage(this BlobsSupportMode mode) => mode is BlobsSupportMode.Storage or BlobsSupportMode.StorageWithReorgs;
    public static bool IsEnabled(this BlobsSupportMode mode) => mode is not BlobsSupportMode.Disabled;
    public static bool IsDisabled(this BlobsSupportMode mode) => mode is BlobsSupportMode.Disabled;
    public static bool SupportsReorgs(this BlobsSupportMode mode) => mode is BlobsSupportMode.StorageWithReorgs;
}
