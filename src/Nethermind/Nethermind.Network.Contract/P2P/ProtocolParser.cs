// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Network.Contract.P2P;

public static class ProtocolParser
{
    // Packed little-endian keys (b0 | b1<<8 | b2<<16 ...)
    private const ushort AA = 0x6161; // "aa"

    private const uint Eth = 0x687465u; // "eth"
    private const uint P2p = 0x703270u; // "p2p"
    private const uint Shh = 0x686873u; // "shh"
    private const uint Bzz = 0x7A7A62u; // "bzz"
    private const uint Par = 0x726170u; // "par"
    private const uint Ndm = 0x6D646Eu; // "ndm"

    private const uint Snap = 0x70616E73u; // "snap"
    private const ulong Nodedata = 0x6174616465646F6Eul; // "nodedata"

    public static bool TryGetProtocolCode(ReadOnlySpan<byte> protocolSpan, [NotNullWhen(true)] out string? protocol)
    {
        protocol = null;

        // Bucket by size first - removes repeated length checks and helps bounds-check elimination.
        switch (protocolSpan.Length)
        {
            case 3:
                // Build a 24-bit key - JIT can eliminate bounds checks because Length == 3.
                uint key3 = (uint)protocolSpan[0]
                         | ((uint)protocolSpan[1] << 8)
                         | ((uint)protocolSpan[2] << 16);

                // Put likely hits first if you know your traffic profile.
                switch (key3)
                {
                    case Eth:
                        protocol = Protocol.Eth; return true;
                    case P2p:
                        protocol = Protocol.P2P; return true;
                    case Shh:
                        protocol = Protocol.Shh; return true;
                    case Bzz:
                        protocol = Protocol.Bzz; return true;
                    case Par:
                        protocol = Protocol.Par; return true;
                    case Ndm:
                        protocol = Protocol.Ndm; return true;
                }
                break;

            case 4:
                if (BinaryPrimitives.ReadUInt32LittleEndian(protocolSpan) == Snap)
                {
                    protocol = Protocol.Snap;
                    return true;
                }
                break;

            case 8:
                if (BinaryPrimitives.ReadUInt64LittleEndian(protocolSpan) == Nodedata)
                {
                    protocol = Protocol.NodeData;
                    return true;
                }
                break;

            case 2:
                // Manual pack is fine too, but BinaryPrimitives is also OK here.
                ushort key2 = (ushort)(protocolSpan[0] | (protocolSpan[1] << 8));
                if (key2 == AA)
                {
                    protocol = Protocol.AA;
                    return true;
                }
                break;
        }
        return false;
    }
}
