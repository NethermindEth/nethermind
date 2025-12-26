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
    /// The span accessor.
    /// </summary>
    Span<byte> Span { get; }

    /// <summary>
    /// The estimated memory size.
    /// </summary>
    int MemorySize { get; }
}
