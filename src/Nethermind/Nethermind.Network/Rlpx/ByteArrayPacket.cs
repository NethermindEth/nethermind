// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;

namespace Nethermind.Network.Rlpx;

/// <summary>
/// A <see cref="ZeroPacket"/> over the data of a <see cref="Packet"/>, which it does not copy.
/// </summary>
internal sealed class ByteArrayPacket : ZeroPacket, IMemoryOwner<byte>
{
    private readonly byte[] _data;

    public ByteArrayPacket(Packet packet)
    {
        _data = packet.Data;
        Protocol = packet.Protocol;
        PacketType = (byte)packet.PacketType;
    }

    public override Memory<byte> Content => _data;

    // The garbage collector keeps the array alive, so the packet can lend itself.
    public override IMemoryOwner<byte> LeaseContent() => this;

    Memory<byte> IMemoryOwner<byte>.Memory => _data;

    void IDisposable.Dispose() { }
}
