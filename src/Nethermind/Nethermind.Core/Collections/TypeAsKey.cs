// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Collections;

public readonly struct TypeAsKey(Type key) : IEquatable<TypeAsKey>
{
    private readonly Type _key = key;

    public static implicit operator Type(TypeAsKey key) => key._key;
    public static implicit operator TypeAsKey(Type key) => new(key);

    public bool Equals(TypeAsKey other) => ReferenceEquals(_key, other._key);
    public override int GetHashCode() => _key?.GetHashCode() ?? 0;
    public override bool Equals(object? obj) => obj is TypeAsKey && Equals((TypeAsKey)obj);
}
