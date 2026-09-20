// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;

namespace Nethermind.BeaconChain.Api.Common;

/// <summary>
/// The API layer's boundary around <see cref="BeaconStateCodec"/>: the codec documents its
/// <see cref="NotSupportedException"/> as meaning exactly one thing (a fork this driver cannot
/// process), and this is where that documented meaning is turned into the API-owned
/// <see cref="UnsupportedForkException"/>. Every API decode of a stored state must come through
/// here; a direct codec call would surface the same condition as a bare 500 again.
/// </summary>
internal static class ApiStateDecoding
{
    public static BeaconStateFulu Decode(ReadOnlySpan<byte> ssz, BeaconChainSpec spec)
    {
        try
        {
            return BeaconStateCodec.Decode(ssz, spec);
        }
        catch (NotSupportedException e)
        {
            throw new UnsupportedForkException(e);
        }
    }
}
