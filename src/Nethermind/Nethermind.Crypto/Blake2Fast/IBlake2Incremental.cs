//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;

namespace Nethermind.Crypto.Blake2Fast;

/// <summary>
///     Code adapted from Blake2Fast (https://github.com/saucecontrol/Blake2Fast)
/// </summary>
public interface IBlake2Incremental
{
    /// <summary>The hash digest length for this instance, in bytes.</summary>
    int DigestLength { get; }

    /// <summary>Update the hash state with the bytes of all values in <paramref name="input" />.</summary>
    /// <param name="input">The message bytes to add to the hash state.</param>
    /// <typeparam name="T">The type of the data that will be added to the hash state.  It must be a value type and must not contain any reference type fields.</typeparam>
    /// <remarks>
    ///   The <typeparamref name="T" /> value will be added to the hash state in memory layout order, including any padding bytes.
    ///   Use caution when using this overload with non-primitive structs or when hash values are to be compared across machines with different struct layouts or byte orders.
    ///   <see cref="byte"/> is the only type that is guaranteed to be consistent across platforms.
    /// </remarks>
    /// <exception cref="NotSupportedException">Thrown when <typeparamref name="T"/> is a reference type or contains any fields of reference types.</exception>
    void Update<T>(ReadOnlySpan<T> input) where T : struct;

    /// <inheritdoc cref="Update{T}(ReadOnlySpan{T})" />
    void Update<T>(Span<T> input) where T : struct;

    /// <inheritdoc cref="Update{T}(ReadOnlySpan{T})" />
    void Update<T>(ArraySegment<T> input) where T : struct;

    /// <inheritdoc cref="Update{T}(ReadOnlySpan{T})" />
    void Update<T>(T[] input) where T : struct;

    /// <summary>Update the hash state with the bytes of the <paramref name="input" /> value.</summary>
    /// <inheritdoc cref="Update{T}(ReadOnlySpan{T})" />
    void Update<T>(T input) where T : struct;

    /// <summary>Finalize the hash, and return the computed digest.</summary>
    /// <returns>The computed hash digest.</returns>
    byte[] Finish();

    /// <inheritdoc cref="TryFinish(Span{byte}, out int)" />
    void Finish(Span<byte> output);

    /// <summary>Finalize the hash, and copy the computed digest to <paramref name="output" />.</summary>
    /// <param name="output">The buffer into which the hash digest should be written.  The buffer must have a capacity of at least <see cref="DigestLength" /> bytes for the method to succeed.</param>
    /// <param name="bytesWritten">On return, contains the number of bytes written to <paramref name="output" />.</param>
    /// <returns>True if the <paramref name="output" /> buffer was large enough to hold the digest, otherwise False.</returns>
    bool TryFinish(Span<byte> output, out int bytesWritten);
}
