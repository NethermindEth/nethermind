// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Sockets;

public interface IMessageBorderPreservingStream
{
    ValueTask<ReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken = default);

    /// <summary>Writes <paramref name="data"/> as the last part of the current message and ends the message.</summary>
    /// <returns>The number of bytes written beyond <paramref name="data"/> to end the message.</returns>
    ValueTask<int> WriteEndOfMessageAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);
}
