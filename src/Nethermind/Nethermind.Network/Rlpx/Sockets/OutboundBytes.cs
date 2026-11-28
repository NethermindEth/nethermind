// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Rlpx.Sockets;

/// <summary>
/// Counts the bytes a connection has taken to send and not yet sent, and tells whether it takes more.
/// </summary>
/// <remarks>
/// It uses the watermarks <see cref="RlpxHost"/> gives DotNetty, with the comparisons of DotNetty's
/// <c>ChannelOutboundBuffer</c>: no longer writable at the high watermark, writable again at the low one. The
/// connection updates it under its lock; <see cref="IsWritable"/> can be read from any thread.
/// </remarks>
internal sealed class OutboundBytes
{
    public const long HighWaterMark = 3_000_000;
    public const long LowWaterMark = 1_000_000;

    private long _pending;
    private volatile bool _writable = true;
    private bool _closed;

    /// <summary>Gets whether the connection takes more packets: false from the high watermark until the low one, and once closed.</summary>
    public bool IsWritable => _writable;

    public long Pending => _pending;

    /// <summary>Charges bytes taken to send, or releases sent ones with a negative count.</summary>
    public void Add(long bytes)
    {
        _pending += bytes;
        if (_writable && _pending >= HighWaterMark)
        {
            _writable = false;
        }
        else if (!_writable && !_closed && _pending <= LowWaterMark)
        {
            _writable = true;
        }
    }

    /// <summary>Stops taking packets for good.</summary>
    public void Close()
    {
        _closed = true;
        _writable = false;
    }
}
