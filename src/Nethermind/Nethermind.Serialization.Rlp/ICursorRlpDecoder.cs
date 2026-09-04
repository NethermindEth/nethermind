// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Serialization.Rlp;

/// <summary>Decodes one item at a cursor passed by value.</summary>
/// <remarks>
/// The <see cref="IRlpDecoder{T}"/> element decoders take the reader by reference, which forces the
/// cursor back into <see cref="RlpReader.Position"/> at every element boundary of an array walk. A
/// static abstract member is a constrained call instead: it binds per instantiation, so the cursor
/// travels in a register through the whole loop and the reader's field is touched once at each end.
/// Decoders opt in; <see cref="IRlpDecoder{T}"/> is unaffected.
/// </remarks>
/// <typeparam name="T">The decoded item type.</typeparam>
public interface ICursorRlpDecoder<T>
{
    /// <summary>Decodes one item starting at <paramref name="position"/>.</summary>
    /// <returns>The position past the item.</returns>
    static abstract int DecodeItem(ReadOnlySpan<byte> data, int position, out T? value);
}
