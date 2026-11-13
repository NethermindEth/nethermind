// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;

namespace Nethermind.Network;

/// <summary>
/// A <see cref="IMessageSerializer{T}"/> whose messages can keep referencing the received bytes instead of copying them.
/// </summary>
/// <typeparam name="T">The message type.</typeparam>
public interface IOwningMessageSerializer<T> : IMessageSerializer<T> where T : MessageBase
{
    /// <summary>
    /// Reads a message from <paramref name="data"/>, which lies within the memory of <paramref name="owner"/>.
    /// </summary>
    /// <param name="owner">
    /// The owner of the received bytes. Ownership passes to this call: the message takes it, or the call disposes it,
    /// including when it throws.
    /// </param>
    /// <param name="data">The message bytes.</param>
    T Deserialize(IMemoryOwner<byte> owner, Memory<byte> data);
}
