// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core;

/// <summary>A store that can start reading keys into its cache before the reads that need them.</summary>
public interface IReadPrefetcher
{
    /// <summary>Starts reading a buffer of fixed-length keys without returning their values.</summary>
    /// <remarks>
    /// Returns before the reads complete and copies the keys first. A failed prefetch is ignored: the read that
    /// later needs the value performs it again.
    /// </remarks>
    void Prefetch(ReadOnlySpan<byte> keys, int keyLength);

    /// <summary>
    /// Whether a prefetch is worth preparing now. False while recent prefetches found their keys cached; true
    /// from time to time even then, to notice when reads turn cold.
    /// </summary>
    bool ShouldPrefetch();
}
