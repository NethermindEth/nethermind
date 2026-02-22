// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Nethermind.Core;

[Serializable]
public sealed class GenericEqualityComparer<T> : EqualityComparer<T> where T : IEquatable<T>?
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override bool Equals(T? x, T? y) => x != null ? y != null && x.Equals(y) : y == null;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode([DisallowNull] T obj) => obj?.GetHashCode() ?? 0;
    public override bool Equals([NotNullWhen(true)] object? obj) => obj != null && GetType() == obj.GetType();
    public override int GetHashCode() => GetType().GetHashCode();
}
