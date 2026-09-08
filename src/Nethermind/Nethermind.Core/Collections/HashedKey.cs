// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;

namespace Nethermind.Core.Collections;

public readonly record struct HashedKey<T>(T Key) where T : IEquatable<T>
{
    private readonly int _hashCode = Key.GetHashCode();
    public bool Equals(HashedKey<T> other) => Key.Equals(other.Key);
    public override int GetHashCode() => _hashCode;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator T(HashedKey<T> value) => value.Key;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator HashedKey<T>(T value) => new(value);
}
