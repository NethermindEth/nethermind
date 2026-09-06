// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Nethermind.Core.Collections;

namespace Nethermind.Network.Discovery.Discv5;

internal readonly struct PooledUdpReceiveResult(IPEndPoint remoteEndPoint, ArrayPoolSpan<byte> buffer)
{
    private readonly bool _hasBuffer = true;
    private readonly ArrayPoolSpan<byte> _buffer = buffer;

    public ReadOnlyMemory<byte> Buffer => _buffer.AsReadOnlyMemory();

    public IPEndPoint RemoteEndPoint { get; } = remoteEndPoint;

    internal void Dispose()
    {
        if (_hasBuffer)
        {
            _buffer.Dispose();
        }
    }
}
