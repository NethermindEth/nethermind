// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core;

public readonly struct Either<T1, T2>
{
    [Obsolete("Do not use", error: true)]
    public Either() { }

    public Either(T1 val)
    {
        Value1 = val;
        Value2 = default;
    }

    public Either(T2 val)
    {
        Value2 = val;
        Value1 = default;
    }

    public T1? Value1 { get; }
    public T2? Value2 { get; }

    public static implicit operator Either<T1, T2>(T1 val) => new(val);
    public static implicit operator Either<T1, T2>(T2 val) => new(val);

    public bool Is(out T1? val)
    {
        val = Value1;
        return Value1 != null;
    }

    public bool Is(out T2? val)
    {
        val = Value2;
        return Value2 != null;
    }

    public void To(out T1 val) => val = Value1 ?? throw new ArgumentException(nameof(T1));

    public void To(out T2 val) => val = Value2 ?? throw new ArgumentException(nameof(T2));
}
