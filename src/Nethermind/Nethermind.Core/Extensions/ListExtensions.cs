// SPDX-FileCopyrightText:2023 Demerzel Solutions Limited
// SPDX-License-Identifier:LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Nethermind.Core.Extensions;

public static class ListExtensions
{
    public static Span<T> AsSpan<T>(this List<T> list) => CollectionsMarshal.AsSpan(list);
}
