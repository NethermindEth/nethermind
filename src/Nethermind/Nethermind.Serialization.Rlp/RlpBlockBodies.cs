// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;

namespace Nethermind.Core;

/// <summary>
/// A list of RLP-backed block bodies that decode lazily on access.
/// </summary>
/// <remarks>
/// Must be explicitly disposed or the backing memory (e.g. a retained network buffer) leaks. The indexer
/// decodes on first access and may throw <see cref="Serialization.Rlp.RlpException"/> on malformed bytes;
/// decoded bodies become invalid after <see cref="Dispose"/> unless <see cref="Disown"/> was called first.
/// Null slots represent bodies absent from the response.
/// </remarks>
public class RlpBlockBodies(RlpBlockBody?[] bodies, IDisposable? sharedMemoryOwner) : IDisposable, IReadOnlyList<BlockBody?>
{
    public static RlpBlockBodies Empty { get; } = new([], null);

    private IDisposable? _sharedMemoryOwner = sharedMemoryOwner;
    private bool _disposed;

    public static RlpBlockBodies FromBodies(BlockBody?[] bodies)
    {
        RlpBlockBody?[] rlpBodies = new RlpBlockBody?[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            rlpBodies[i] = bodies[i] is { } body ? RlpBlockBody.FromBody(body) : null;
        }

        return new RlpBlockBodies(rlpBodies, null);
    }

    public int Count => bodies.Length;

    public BlockBody? this[int index] => bodies[index]?.Decode();

    public RlpBlockBody? GetRawBody(int index) => bodies[index];

    /// <summary>
    /// Disconnects all bodies from the backing memory (copying transaction data out) and releases it,
    /// so the decoded bodies stay usable indefinitely. A later <see cref="Dispose"/> is a no-op.
    /// </summary>
    public void Disown()
    {
        if (_disposed) return;

        foreach (RlpBlockBody? body in bodies)
        {
            body?.DetachDecoded();
        }

        Dispose();
    }

    /// <summary>
    /// Returns transactions of bodies decoded via the indexer to the pool and releases the backing memory.
    /// Never decodes, so it is safe on responses that were dropped without being read.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (RlpBlockBody? body in bodies)
        {
            body?.Dispose();
        }

        _sharedMemoryOwner?.Dispose();
        _sharedMemoryOwner = null;
    }

    public IEnumerator<BlockBody?> GetEnumerator()
    {
        for (int i = 0; i < Count; i++)
        {
            yield return this[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
