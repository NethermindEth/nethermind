// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;

namespace Nethermind.Core;

public static partial class GenericComparer
{
    public static partial IComparer<T>? GetOptimized<T>(IComparer<T>? comparer) => comparer;

    public static partial IComparer<T>? GetOptimized<T>() where T : IComparable<T>? => GenericComparer<T>.Default;
}
