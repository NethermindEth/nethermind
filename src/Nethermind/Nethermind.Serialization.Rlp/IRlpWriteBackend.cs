// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Serialization.Rlp;

/// <summary>
/// Primitive append-only write target used by RLP writer extension methods.
/// </summary>
public interface IRlpWriteBackend
{
    /// <summary>
    /// Appends one byte and advances the backend position by one.
    /// </summary>
    void WriteByte(byte byteToWrite);

    /// <summary>
    /// Appends all supplied bytes before returning. Implementations must not retain the span.
    /// </summary>
    void Write(scoped ReadOnlySpan<byte> bytesToWrite);

    /// <summary>
    /// Appends <paramref name="length"/> zero bytes and advances the backend position by that length.
    /// </summary>
    void WriteZero(int length);
}
