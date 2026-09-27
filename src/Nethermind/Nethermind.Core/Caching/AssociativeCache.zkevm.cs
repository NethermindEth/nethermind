// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Core.Caching;

public sealed partial class AssociativeCache<TKey, TValue>
{
    /// <inheritdoc cref="TrySettleRead"/>
    /// <remarks>
    /// The guest runs single-threaded, so no entry is seen mid-write and waiting for one would only cost steps.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static partial bool TrySettleRead(ref Entry entry, long h1, long h2, long expectedTag, ref TKey key, ref TValue? value) =>
        h1 == h2;
}
