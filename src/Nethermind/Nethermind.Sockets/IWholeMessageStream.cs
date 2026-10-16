// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Sockets;

/// <summary>A message stream that can send a complete, already serialized message in one transport write.</summary>
/// <remarks>
/// <see cref="IMessageBorderPreservingStream.WriteEndOfMessageAsync"/> ends a message with a write of its own. On a
/// WebSocket that is an empty final frame after the payload's frames, and the receiver completes the message only when
/// that frame arrives.
/// </remarks>
public interface IWholeMessageStream
{
    /// <summary>Sends <paramref name="message"/> as one complete message.</summary>
    /// <remarks>A failure may leave part of the message on the connection.</remarks>
    ValueTask WriteMessageAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default);
}
