// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Network.Discovery.RoutingTable;

public class NodeDistanceCalculator : INodeDistanceCalculator
{
    private readonly int _maxDistance;

    public NodeDistanceCalculator(IDiscoveryConfig discoveryConfig)
    {
        _maxDistance = discoveryConfig.BucketsCount;
    }

    public int CalculateDistance(Hash256 sourceId, Hash256 destinationId)
    {
        Vector256<byte> a = Unsafe.As<ValueHash256, Vector256<byte>>(ref Unsafe.AsRef(in sourceId.ValueHash256));
        Vector256<byte> b = Unsafe.As<ValueHash256, Vector256<byte>>(ref Unsafe.AsRef(in destinationId.ValueHash256));
        Vector256<byte> xor = Vector256.Xor(a, b);
        int leadingZeros = xor.CountLeadingZeroBits();
        return Math.Max(0, _maxDistance - leadingZeros);
    }
}
