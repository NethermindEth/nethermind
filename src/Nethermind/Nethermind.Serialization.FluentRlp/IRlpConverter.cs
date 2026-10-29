// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Serialization.FluentRlp;

/// <summary>Reads and writes values of <typeparamref name="T"/> as RLP.</summary>
/// <remarks>
/// The members are static, so calls bind at compile time and need no instance.
/// <see cref="RlpSerializableAttribute"/> generates an implementation for a record.
/// </remarks>
public interface IRlpConverter<T> where T : allows ref struct
{
    /// <summary>Reads a value from <paramref name="reader"/>.</summary>
    /// <exception cref="RlpReaderException">The input is not a canonical encoding of <typeparamref name="T"/>.</exception>
    public static abstract T Read(ref RlpReader reader);

    /// <summary>Writes <paramref name="value"/> to <paramref name="writer"/>.</summary>
    public static abstract void Write(ref RlpWriter writer, T value);
}

/// <summary>An <see cref="IRlpConverter{T}"/> that also computes the encoded length of a value without writing it.</summary>
/// <remarks>
/// <see cref="Rlp.Write{TConverter, T}(T)"/> uses the length to allocate the exact output and write it in one pass,
/// without a buffer copy. <see cref="RlpSerializableAttribute"/> generates it for a record whose field lengths can be
/// computed.
/// </remarks>
public interface IRlpLengthConverter<T> : IRlpConverter<T> where T : allows ref struct
{
    /// <summary>Gets the number of bytes that <see cref="IRlpConverter{T}.Write"/> writes for <paramref name="value"/>.</summary>
    public static abstract int GetLength(T value);
}
