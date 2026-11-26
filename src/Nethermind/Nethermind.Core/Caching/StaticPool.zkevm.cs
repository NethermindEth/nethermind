// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Resettables;

namespace Nethermind.Core.Caching;

public static partial class StaticPool<T> where T : class, IResettable, new()
{
    /// <summary>
    /// The guest runs on one thread, so a plain queue replaces the concurrent one.
    /// </summary>
    private static Queue<T> _pool => field ??= [];
}
