// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nethermind.Core;

namespace Nethermind.State;

/// <summary>
/// A not so managed reference to <see cref="StorageValue"/>.
/// Allows quick equality checks and ref returning semantics.
/// </summary>
public readonly unsafe struct StorageValuePtr : IEquatable<StorageValuePtr>
{
    private readonly StorageValue* _pointer;

    public StorageValuePtr(StorageValue* pointer)
    {
        _pointer = pointer;
    }

    public bool IsZero => _pointer == null;

    public static readonly StorageValuePtr Null = default;

    public ref readonly StorageValue Ref =>
        ref _pointer == null ? ref StorageValue.Zero : ref Unsafe.AsRef<StorageValue>(_pointer);

    public override bool Equals([NotNullWhen(true)] object? obj)
    {
        return obj is StorageValuePtr other && Equals(other);
    }

    public bool Equals(StorageValuePtr other) => _pointer == other._pointer;

    public override int GetHashCode() => unchecked((int)(long)_pointer);

    public override string ToString()
    {
        return IsZero ? "0" : $"{Ref} @ {new UIntPtr(_pointer)}";
    }

    public void SetValue(in StorageValue value)
    {
        *_pointer = value;
    }
}
