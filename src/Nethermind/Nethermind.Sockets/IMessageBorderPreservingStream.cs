// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;

namespace Nethermind.Sockets;

public interface IMessageBorderPreservingStream
{
    Task<ReceiveResult?> ReceiveAsync(ArraySegment<byte> buffer);
    Task<int> WriteEndOfMessageAsync();
}
