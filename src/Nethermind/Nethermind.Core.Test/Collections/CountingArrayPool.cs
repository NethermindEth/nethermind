// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Threading;

namespace Nethermind.Core.Test.Collections;

/// <summary>
/// An <see cref="ArrayPool{T}"/> wrapper that tracks the number of outstanding (un-returned) rentals.
/// Inject into any component that accepts an <c>ArrayPool&lt;T&gt;</c> parameter to detect buffer leaks
/// in tests.
///
/// <para>
/// After running the code under test, inspect <see cref="Outstanding"/> to determine how many
/// rented buffers were never returned. A non-zero value indicates a leak.
/// </para>
///
/// <para>
/// Thread-safe: uses Interlocked for counter updates so it can be shared across concurrent operations.
/// </para>
/// </summary>
/// <typeparam name="T">The array element type.</typeparam>
public sealed class CountingArrayPool<T> : ArrayPool<T>
{
    private readonly ArrayPool<T> _inner;
    private int _rentCount;
    private int _returnCount;

    /// <summary>
    /// Creates a counting wrapper around an existing pool.
    /// </summary>
    /// <param name="inner">The underlying pool to delegate to. If null, uses <see cref="ArrayPool{T}.Shared"/>.</param>
    public CountingArrayPool(ArrayPool<T>? inner = null)
    {
        _inner = inner ?? ArrayPool<T>.Shared;
    }

    /// <summary>Total number of Rent calls made since creation.</summary>
    public int RentCount => Volatile.Read(ref _rentCount);

    /// <summary>Total number of Return calls made since creation.</summary>
    public int ReturnCount => Volatile.Read(ref _returnCount);

    /// <summary>
    /// Number of buffers currently rented but not yet returned.
    /// A positive value after the code under test completes indicates a buffer leak.
    /// </summary>
    public int Outstanding => RentCount - ReturnCount;

    /// <inheritdoc/>
    public override T[] Rent(int minimumLength)
    {
        Interlocked.Increment(ref _rentCount);
        return _inner.Rent(minimumLength);
    }

    /// <inheritdoc/>
    public override void Return(T[] array, bool clearArray = false)
    {
        Interlocked.Increment(ref _returnCount);
        _inner.Return(array, clearArray);
    }
}
