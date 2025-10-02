// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core;

public readonly struct Either<T1, T2>
{
    private readonly T1? value1;
    private readonly T2? value2;

    [Obsolete("Do not use", error: true)]
    public Either() { }

    public Either(T1 val)
    {
        value1 = val;
        value2 = default;
    }

    public Either(T2 val)
    {
        value2 = val;
        value1 = default;
    }

    public static implicit operator Either<T1, T2>(T1 val) => new(val);
    public static implicit operator Either<T1, T2>(T2 val) => new(val);

    public bool Is(out T1? val)
    {
        val = value1;
        return value1 is not null;
    }

    public bool Is(out T2? val)
    {
        val = value2;
        return value2 is not null;
    }

    public void To(out T1 val) => val = value1 ?? throw new ArgumentException(nameof(T1));

    public void To(out T2 val) => val = value2 ?? throw new ArgumentException(nameof(T2));
}
