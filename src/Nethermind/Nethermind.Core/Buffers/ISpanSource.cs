// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Buffers;

/// <summary>
/// A tiny array, allocating just enough space to hold the payload with a byte-long length.
/// </summary>
public interface ISpanSource
{
    /// <summary>
    /// The length of the underlying payload.
    /// </summary>
    int Length { get; }

    /// <summary>
    /// Like <see cref="MemoryExtensions.SequenceEqual{T}(System.ReadOnlySpan{T},System.ReadOnlySpan{T})"/>
    /// </summary>
    bool SequenceEqual(ReadOnlySpan<byte> other);

    /// <summary>
    /// Like <see cref="MemoryExtensions.CommonPrefixLength{T}(System.ReadOnlySpan{T},System.ReadOnlySpan{T})"/>
    /// </summary>
    int CommonPrefixLength(ReadOnlySpan<byte> other);

    /// <summary>
    /// The span accessor.
    /// </summary>
    Span<byte> Span { get; }
}
