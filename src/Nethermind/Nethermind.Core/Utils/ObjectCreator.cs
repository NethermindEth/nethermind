// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq.Expressions;
using System.Reflection;

namespace Nethermind.Core;

public static class ObjectCreator<T> where T : new()
{
    // Cache in readonly static
    public static Func<T> Create { get; } = BuildCreator();

    private static Func<T> BuildCreator()
    {
        // find the public parameterless ctor (throws if none)
        ConstructorInfo? ci = typeof(T).GetConstructor(BindingFlags.Public | BindingFlags.Instance, []) ??
            throw new InvalidOperationException($"{typeof(T).Name} has no parameterless ctor.");

        NewExpression newExpr = Expression.New(ci);
        Expression<Func<T>> lambda = Expression.Lambda<Func<T>>(
            newExpr,
            name: $"Create{typeof(T).Name}",
            tailCall: false,
            parameters: Array.Empty<ParameterExpression>()
        );
        return lambda.Compile();
    }
}
