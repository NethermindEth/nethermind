// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Trie.Sparse;

public enum LeafUpdateKind : byte
{
    None = 0,
    Changed = 1,
    Deleted = 2,
    Touched = 3,
}

/// <summary>
/// Represents a pending update to a trie leaf. Use factory methods — <c>default(LeafUpdate)</c> is invalid.
/// </summary>
public readonly struct LeafUpdate : IEquatable<LeafUpdate>
{
    public LeafUpdateKind Kind { get; }
    private readonly byte[]? _value;

    private LeafUpdate(LeafUpdateKind kind, byte[]? value)
    {
        Kind = kind;
        _value = value;
    }

    /// <summary>Non-null for Changed, null for Deleted/Touched.</summary>
    public byte[]? Value => _value;

    public bool IsDelete => Kind == LeafUpdateKind.Deleted;
    public bool IsValid => Kind != LeafUpdateKind.None;

    /// <param name="value">Must be non-null and non-empty. For deletion, use <see cref="Deleted"/>.</param>
    public static LeafUpdate Changed(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0)
            throw new ArgumentException("Use LeafUpdate.Deleted() for empty values.", nameof(value));
        return new LeafUpdate(LeafUpdateKind.Changed, value);
    }

    public static LeafUpdate Deleted() => new(LeafUpdateKind.Deleted, null);

    public static LeafUpdate Touched() => new(LeafUpdateKind.Touched, null);

    public bool Equals(LeafUpdate other) => Kind == other.Kind && ReferenceEquals(_value, other._value);
    public override bool Equals(object? obj) => obj is LeafUpdate other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Kind, _value?.GetHashCode() ?? 0);
    public override string ToString() => Kind switch
    {
        LeafUpdateKind.Changed => $"Changed({_value?.Length ?? 0}B)",
        LeafUpdateKind.Deleted => "Deleted",
        LeafUpdateKind.Touched => "Touched",
        _ => "None(invalid)"
    };
}
