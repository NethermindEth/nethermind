// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Core.Caching;

public sealed partial class AssociativeKeyCache<TKey>
{
    /// <remarks>
    /// Stops at the first ticker at least as new, but proving an entry newest reads all eight, up to seven lines past the
    /// matched way; that trades loads for the clock read and the shared ticker write. A skipped hit keeps its older stamp,
    /// so a later stamp in the same clock tick ranks after it, as in access order, where stamping every hit would tie them.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static partial void CheckNewestInSet(ref Entry entries, int baseIdx, int way, long ticker, ref bool newest)
    {
        for (int j = 0; j < Ways; j++)
        {
            if (j != way && Unsafe.Add(ref entries, baseIdx + j).Ticker >= ticker) return;
        }

        newest = true;
    }}