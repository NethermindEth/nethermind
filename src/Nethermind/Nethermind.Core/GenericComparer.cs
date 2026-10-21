// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Nethermind.Core;

[Serializable]
public sealed class GenericComparer<T> : Comparer<T>, GenericComparer.IGenericComparer where T : IComparable<T>?
{
    public new static GenericComparer<T> Default { get; } = new();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int Compare(T? x, T? y) => x is not null ? y is not null ? x.CompareTo(y) : 1 : y is null ? 0 : -1;
    public override bool Equals(object? obj) => obj != null && GetType() == obj.GetType();
    public override int GetHashCode() => GetType().GetHashCode();
}

public static partial class GenericComparer
{
    internal interface IGenericComparer;

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static partial IComparer<T>? GetOptimized<T>(IComparer<T>? comparer);

    // Returns null on JIT/CoreCLR so collection constructors fall back to Comparer<T>.Default
    // (which the JIT recognizes via [Intrinsic] and devirtualizes at its own call sites).
    // On bflat/AOT, returns our reflection-free fallback since Comparer<T>.Default is unavailable.
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static partial IComparer<T>? GetOptimized<T>() where T : IComparable<T>?;
}
