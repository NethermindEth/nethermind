// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Optimism.CL.P2P;

public static class PayloadVersion
{
    public const uint Ecotone = 1;
    public const uint Isthmus = 2;

    public static bool TryParse(ReadOnlySpan<byte> data, [NotNullWhen(true)] out uint? payloadVersion)
    {
        var version = BinaryPrimitives.ReadUInt32LittleEndian(data);

        switch (version)
        {
            case Ecotone:
            case Isthmus:
                payloadVersion = version;
                return true;
            default:
                payloadVersion = null;
                return false;
        }
    }
}
