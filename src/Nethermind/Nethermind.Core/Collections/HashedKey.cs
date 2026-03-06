// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Collections;

public readonly struct HashedKey<T>(T key) : IEquatable<HashedKey<T>> where T : IEquatable<T>
{
    public readonly T Key = key;
    private readonly int _hashCode = key.GetHashCode();
    public bool Equals(HashedKey<T> other) => Key.Equals(other.Key);
    public override bool Equals(object? obj) => obj is HashedKey<T> other && Equals(other);
    public override int GetHashCode() => _hashCode;
}
