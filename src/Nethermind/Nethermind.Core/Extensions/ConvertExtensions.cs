// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Extensions;

public static class ConvertExtensions
{
    public static T ConvertTo<T>(this object value)
    {
        if (value is T asT) return asT;
        if (value is IConvertible<T> convertible) return convertible.Convert();
        throw new InvalidOperationException($"No way to convert {value.GetType()} to {typeof(T)}");
    }
}
