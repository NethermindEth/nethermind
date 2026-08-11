// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Specs.Forks;

public class Bogota() : NamedReleaseSpec<Bogota>(Amsterdam.Instance)
{
    public override void Apply(NamedReleaseSpec spec)
    {
        spec.Name = "Bogota";
        // Kept off the Amsterdam fork class so Amsterdam's genesis and its consensus test vectors stay
        // unchanged; a frames-only devnet activates frame txs via eip8141TransitionTimestamp instead.
        spec.IsEip8141Enabled = true;
        spec.IsEip7805Enabled = true;
        spec.EngineApiNewPayloadVersion = EngineApiVersions.NewPayload.V6;
        spec.EngineApiForkchoiceVersion = EngineApiVersions.Fcu.V5;
    }
}
