// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;

namespace Nethermind.Core.Buffers;

public static class ArraySegmentExtensions
{
    public static int IndexOf<T>(this ArraySegment<T> arraySegment, T item) => ((IList<T>)arraySegment).IndexOf(item);
}
