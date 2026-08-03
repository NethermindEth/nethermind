// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Benchmarks.State.FlatBase;

/// <summary>
/// A storage backend under test in the flat-base point-read benchmark: uniform point reads of
/// account (20-byte key) and storage-slot (52-byte key) records.
/// </summary>
public interface IFlatPointReadBackend : IDisposable
{
    /// <summary>Open a read session. Sessions are cheap and not thread-safe — each reader thread
    /// creates (and disposes) its own; for LMDB a session wraps a read transaction.</summary>
    IFlatReadSession BeginSession();
}

/// <summary>Per-thread read handle of an <see cref="IFlatPointReadBackend"/>.</summary>
public interface IFlatReadSession : IDisposable
{
    /// <summary>Read the account record at <paramref name="key20"/> into <paramref name="valueOut"/>.</summary>
    /// <returns>The value length in bytes, or 0 on a miss.</returns>
    int GetAccount(ReadOnlySpan<byte> key20, Span<byte> valueOut);

    /// <summary>Read the storage-slot record at <paramref name="key52"/> into <paramref name="valueOut"/>.</summary>
    /// <returns>The value length in bytes, or 0 on a miss.</returns>
    int GetSlot(ReadOnlySpan<byte> key52, Span<byte> valueOut);
}
