// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Collections;

/// <summary>
/// A generic container for any reference type that implements IEquatable{T}.
/// The Box can contain either a reference or null and implements equality methods 
/// that delegate to the wrapped instance.
/// </summary>
/// <typeparam name="T">The reference type that implements IEquatable{T}</typeparam>
public readonly struct Box<T>(T? key) : IEquatable<Box<T>> where T : class, IEquatable<T>
{
    private readonly T? _key = key;

    public T? Value => _key;

    public static implicit operator T?(Box<T> box) => box._key;
    public static implicit operator Box<T>(T? key) => new(key);

    public bool Equals(Box<T> other) => _key is null ? other._key is null : _key.Equals(other._key);

    public override bool Equals(object? obj) => obj is Box<T> box && Equals(box);

    public override int GetHashCode() => _key?.GetHashCode() ?? 0;

    public override string ToString() => _key?.ToString() ?? "<null>";
}

/// <summary>
/// A specialized version of Box for types that implement both IEquatable{T} and IComparable{T}.
/// </summary>
/// <typeparam name="T">The reference type that implements both IEquatable{T} and IComparable{T}</typeparam>
public readonly struct ComparableBox<T>(T? key) : IEquatable<ComparableBox<T>>, IComparable<ComparableBox<T>> where T : class, IEquatable<T>, IComparable<T>
{
    private readonly T? _key = key;

    public T? Value => _key;

    public static implicit operator T?(ComparableBox<T> box) => box._key;
    public static implicit operator ComparableBox<T>(T? key) => new(key);

    public bool Equals(ComparableBox<T> other) => _key is null ? other._key is null : _key.Equals(other._key);

    public override bool Equals(object? obj) => obj is ComparableBox<T> box && Equals(box);

    public override int GetHashCode() => _key?.GetHashCode() ?? 0;

    public int CompareTo(ComparableBox<T> other)
    {
        if (_key is null)
            return other._key is null ? 0 : -1;
        if (other._key is null)
            return 1;
        return _key.CompareTo(other._key);
    }

    public override string ToString() => _key?.ToString() ?? "<null>";
}

/// <summary>
/// A generic container for any reference type without IEquatable{T} requirement.
/// Uses object.Equals for equality comparison. Useful for types like System.Type or IPAddress.
/// </summary>
/// <typeparam name="T">Any reference type</typeparam>
public readonly struct SimpleBox<T>(T? key) : IEquatable<SimpleBox<T>> where T : class
{
    private readonly T? _key = key;

    public T? Value => _key;

    public static implicit operator T?(SimpleBox<T> box) => box._key;
    public static implicit operator SimpleBox<T>(T? key) => new(key);

    public bool Equals(SimpleBox<T> other) =>
        _key is null ? other._key is null : _key.Equals(other._key);

    public override bool Equals(object? obj) => obj is SimpleBox<T> box && Equals(box);

    public override int GetHashCode() => _key?.GetHashCode() ?? 0;

    public override string ToString() => _key?.ToString() ?? "<null>";
}

