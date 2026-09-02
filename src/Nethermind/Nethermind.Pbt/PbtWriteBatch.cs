// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

/// <summary>A batch of complete-key mutations applied atomically by <see cref="TrieUpdater"/>.</summary>
public sealed class PbtWriteBatch
{
    private readonly List<PbtWriteOperation> _operations = [];

    /// <summary>Gets the number of mutations in this batch.</summary>
    public int Count => _operations.Count;

    /// <summary>Adds a complete-key value mutation.</summary>
    public void Set(PbtFullKey key, in ValueHash256 value)
    {
        ArgumentNullException.ThrowIfNull(key);
        _operations.Add(PbtWriteOperation.Set(key, value));
    }

    /// <summary>Adds an explicit complete-key deletion.</summary>
    public void Delete(PbtFullKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        _operations.Add(PbtWriteOperation.Delete(key));
    }

    internal IReadOnlyList<PbtWriteOperation> Operations => _operations;
}

internal enum PbtWriteOperationKind : byte
{
    Set,
    Delete,
}

internal readonly record struct PbtWriteOperation(PbtFullKey Key, ValueHash256 Value, PbtWriteOperationKind Kind)
{
    internal static PbtWriteOperation Set(PbtFullKey key, in ValueHash256 value) => new(key, value, PbtWriteOperationKind.Set);
    internal static PbtWriteOperation Delete(PbtFullKey key) => new(key, default, PbtWriteOperationKind.Delete);
}
