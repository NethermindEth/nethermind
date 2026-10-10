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
            IGenericEqualityComparer => null,
            _ => comparer
        };

    public static partial IEqualityComparer<T>? GetOptimized<T>() where T : IEquatable<T>? => null;
}
