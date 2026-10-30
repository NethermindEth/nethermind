// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Network;

/// <summary>
/// Serializes a message into caller-provided memory and deserializes it from received bytes.
/// </summary>
/// <typeparam name="T">The message type.</typeparam>
public interface IMessageSerializer<T> where T : MessageBase
{
    /// <summary>
    /// Gets the exact number of bytes <see cref="Serialize"/> writes for <paramref name="message"/>.
    /// </summary>
    int GetLength(T message);

    /// <summary>
    /// Writes <paramref name="message"/> to the start of <paramref name="destination"/>.
    /// </summary>
    /// <returns>The number of bytes written, which equals <see cref="GetLength"/>.</returns>
    int Serialize(Span<byte> destination, T message);

    /// <summary>
    /// Reads a message from <paramref name="data"/>.
    /// </summary>
    /// <remarks>The message must not keep references to <paramref name="data"/>, which the caller may reuse.</remarks>
    T Deserialize(ReadOnlySpan<byte> data);
}
