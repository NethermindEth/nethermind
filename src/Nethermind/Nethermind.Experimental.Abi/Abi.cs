// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Experimental.Abi;

public delegate T AbiReadFunc<out T>(ref BinarySpanReader r);
public delegate void AbiWriteAction<in T>(ref BinarySpanWriter w, T value);
public delegate int AbiSizeFunc<in T>(T value);

public class Abi<T>
{
    public required string Name { get; init; }
    public required AbiReadFunc<T> Read { get; init; }
    public required AbiWriteAction<T> Write { get; init; }

    /// <summary>Number of bytes a value of this type takes in the head of an enclosing tuple or array.</summary>
    /// <remarks>
    /// For dynamic types it is always 32, the offset word, whatever was initialized. For static types it is the full encoded size:
    /// 32 for elementary types, <c>k</c> times the element's size for <c>T[k]</c>, the sum of the components' sizes for tuples, and 0 for <c>()</c>.
    /// Defaults to 32.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Initialized with a negative value.</exception>
    public int HeadSize
    {
        get => IsDynamic ? 32 : field;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            field = value;
        }
    } = 32;

    /// <summary>Computes the size of a value's tail, the encoding an offset in the enclosing head points to; <see langword="null"/> for static types.</summary>
    /// <remarks>
    /// Setting it makes the type dynamic. For example, a <c>string</c> tail is a 32-byte length followed by the zero-padded UTF-8 bytes,
    /// and a dynamic tuple's tail is its components' heads followed by their tails.
    /// </remarks>
    public AbiSizeFunc<T>? TailSize { get; init; }

    /// <summary>
    /// <see langword="true"/> exactly when <see cref="TailSize"/> is set: values are then encoded out of line,
    /// with the enclosing head holding an offset to their contents in the tail.
    /// </summary>
    public bool IsDynamic => TailSize is not null;

    /// <summary>Returns the length of <paramref name="value"/>'s encoding, not counting the offset that refers to it when nested.</summary>
    /// <remarks>
    /// That is <see cref="TailSize"/> for dynamic types and <see cref="HeadSize"/> otherwise.
    /// <see cref="AbiSignature{T}.Size(T)"/> also counts the method ID.
    /// </remarks>
    public int Size(T value) => TailSize?.Invoke(value) ?? HeadSize;

    public override string ToString() => Name;
}
