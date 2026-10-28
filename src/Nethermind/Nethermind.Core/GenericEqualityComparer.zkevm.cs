// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;

namespace Nethermind.Core;

public static partial class GenericEqualityComparer
{
    public static partial IEqualityComparer<T>? GetOptimized<T>(IEqualityComparer<T>? comparer) =>
        comparer switch
        {
            // A value-type key calls its own GetHashCode/Equals directly; only reference types need the fallback.
            IGenericEqualityComparer when typeof(T).IsValueType => null,
            _ => comparer
        };

    public static partial IEqualityComparer<T>? GetOptimized<T>() where T : IEquatable<T>? => GenericEqualityComparer<T>.Default;
}
