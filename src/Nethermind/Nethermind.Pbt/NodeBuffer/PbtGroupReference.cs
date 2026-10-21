// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Pbt.NodeBuffer;

/// <summary>A physical descendant group, an unresolved descendant, or no physical descendant.</summary>
/// <remarks>
/// Direct references are local to one buffer. Generation ids are never reused, so a retired reference cannot alias a later record.
/// Direct references order by allocation: a later reservation compares greater than an earlier one.
/// </remarks>
public readonly record struct PbtGroupReference : IComparable<PbtGroupReference>
{
    private readonly ulong _value;

    private PbtGroupReference(ulong value) => _value = value;

    /// <summary>The direct reference of the record at <paramref name="offset"/> in buffer generation <paramref name="generation"/>.</summary>
    public PbtGroupReference(uint generation, int offset) => _value = (ulong)generation << 32 | (uint)offset;

    /// <summary>No physical group is owned by this subtree. Also represents a snapshot group tombstone.</summary>
    public static PbtGroupReference None => default;

    /// <summary>The subtree's first physical group must be resolved against the caller's snapshot.</summary>
    public static PbtGroupReference Unknown => new(1);

    /// <summary>Whether the subtree owns no physical descendant group.</summary>
    public bool IsNone => _value == 0;

    /// <summary>Whether resolving the descendant requires a snapshot lookup.</summary>
    public bool IsUnknown => _value == 1;

    /// <summary>Whether this value identifies a record in a buffer generation.</summary>
    public bool IsDirect => _value > 1;

    /// <inheritdoc/>
    public int CompareTo(PbtGroupReference other) => _value.CompareTo(other._value);

    /// <summary>The buffer generation of a direct reference.</summary>
    public uint Generation => (uint)(_value >> 32);

    /// <summary>The byte offset of a direct reference in its generation.</summary>
    public int Offset => (int)(uint)_value;
}
