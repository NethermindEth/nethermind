// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Db;

// Not thread-safe.
internal sealed class RequireCommitWriteBatch(IWriteBatch batch): IWriteBatch
{
    private bool _completed;

    public void Dispose()
    {
        if (_completed) return;
        _completed = true;

        batch.Clear();
        batch.Dispose();
    }

    public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
    {
        batch.Set(key, value, flags);
    }

    public void Merge(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None)
    {
        batch.Merge(key, value, flags);
    }

    public void Clear()
    {
        batch.Clear();
    }

    public void Commit()
    {
        if (_completed) return;
        _completed = true;

        batch.Dispose();
    }
}
