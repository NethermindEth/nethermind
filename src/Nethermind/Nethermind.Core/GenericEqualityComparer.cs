// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Nethermind.Core;

[Serializable]
public sealed class GenericEqualityComparer<T> : EqualityComparer<T>, GenericEqualityComparer.IGenericEqualityComparer where T : IEquatable<T>?
{
    public new static GenericEqualityComparer<T> Default { get; } = new();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override bool Equals(T? x, T? y) => x != null ? y != null && x.Equals(y) : y == null;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode([DisallowNull] T obj) => obj?.GetHashCode() ?? 0;
    public override bool Equals([NotNullWhen(true)] object? obj) => obj != null && GetType() == obj.GetType();
    public override int GetHashCode() => GetType().GetHashCode();
}

public static class GenericEqualityComparer
{
    internal interface IGenericEqualityComparer;

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static IEqualityComparer<T>? GetOptimized<T>(IEqualityComparer<T>? comparer) =>
        comparer switch
        {
#if !ZK_EVM
            IGenericEqualityComparer => null,
#endif
            _ => comparer
        };

    // Returns null on JIT/CoreCLR so collection constructors fall back to EqualityComparer<T>.Default
    // (which the JIT recognizes via [Intrinsic] and devirtualizes at its own call sites).
    // On bflat/AOT, returns our reflection-free fallback since EqualityComparer<T>.Default is unavailable.
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static IEqualityComparer<T>? GetOptimized<T>() where T : IEquatable<T>? =>
#if ZK_EVM
        GenericEqualityComparer<T>.Default;
#else
        null;
#endif
}
