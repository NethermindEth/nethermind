// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;

namespace Nethermind.TxPool;

/// <summary>
/// Defines blobs support mode.
/// </summary>
public enum BlobsSupportMode
{
    [Description("Disables support for blob transactions.")]
    /// <summary>
    /// No support for blob transactions.
    /// </summary>
    Disabled,

    [Description("Stores the blob transactions in memory only.")]
    /// <summary>
    /// Blob transactions stored only in memory
    /// </summary>
    InMemory,

    [Description("Stores the blob transactions in the permanent storage.")]
    /// <summary>
    /// Blob transactions stored in db.
    /// </summary>
    Storage,

    [Description("Stores the blob transactions in the permanent storage with support for restoring reorganized transactions to the blob pool.")]
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
