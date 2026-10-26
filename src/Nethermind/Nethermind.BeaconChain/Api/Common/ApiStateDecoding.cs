// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;

namespace Nethermind.BeaconChain.Api.Common;

/// <summary>
/// The API layer's boundary around <see cref="BeaconStateCodec"/>: decodes a stored state in the layout of the fork its
/// slot belongs to, and turns a fork this driver has no state type for into the API-owned
/// <see cref="UnsupportedForkException"/>. Every API decode of a stored state must come through here; a direct codec call
/// would surface that condition as a bare 500 again.
/// </summary>
internal static class ApiStateDecoding
{
    /// <exception cref="UnsupportedForkException">The state's slot predates Electra.</exception>
    /// <exception cref="BeaconStateException">The state is too short to carry a slot.</exception>
    /// <exception cref="System.IO.InvalidDataException">The body is malformed for the layout its slot selects.</exception>
    public static ApiState Decode(ReadOnlySpan<byte> ssz, BeaconChainSpec spec)
    {
        ulong slot = BeaconStateCodec.ReadSlot(ssz);
        ulong epoch = spec.GetEpoch(slot);
        if (epoch < spec.ElectraForkEpoch)
        {
            throw new UnsupportedForkException(new NotSupportedException($"Beacon state at slot {slot} predates Electra (fork epoch {spec.ElectraForkEpoch})"));
        }

        // BeaconStateCodec.DecodeForked has no Electra layout: the state transition never processes one.
        if (epoch < spec.FuluForkEpoch)
        {
            BeaconStateElectra.Decode(ssz, out BeaconStateElectra electra);
            return ApiState.Of(BeaconFork.Electra, electra);
        }

        return BeaconStateCodec.DecodeForked(ssz, spec) switch
        {
            ForkedBeaconState.OfFulu fulu => ApiState.Of(BeaconFork.Fulu, fulu.State),
            ForkedBeaconState.OfGloas gloas => ApiState.Of(gloas.State),
            ForkedBeaconState forked => throw new UnsupportedForkException(new NotSupportedException($"Unhandled state shape {forked.GetType().Name}")),
        };
    }
}
