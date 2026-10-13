// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Buffers;

public readonly partial struct CappedArray<T> where T : struct
{
    private static CappedArray<T> _empty;

    public static ref readonly CappedArray<T> Empty
    {
        get
        {
            if (_empty.IsNull) _empty = new([]);
            return ref _empty;
        }
    }

    public static object NullBoxed => field ??= _null;
    public static object EmptyBoxed => field ??= Empty;
}
