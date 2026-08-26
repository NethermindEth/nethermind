// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Specs.Forks;

/// <summary>Devnet fork enabling EIP-8141 frame transactions and EIP-7805 inclusion lists on top of Amsterdam.</summary>
/// <remarks>
/// Both EIPs are kept off the Amsterdam fork class so Amsterdam's genesis and its consensus test
/// vectors stay unchanged; a frames-only devnet activates frame transactions via
/// <c>eip8141TransitionTimestamp</c> instead.
/// </remarks>
public class Bogota() : NamedReleaseSpec<Bogota>(Amsterdam.Instance)
{
    public override void Apply(NamedReleaseSpec spec)
    {
        spec.Name = "Bogota";
        spec.IsEip8141Enabled = true;
        spec.IsEip7805Enabled = true;
        spec.EngineApiNewPayloadVersion = EngineApiVersions.NewPayload.V6;
        spec.EngineApiForkchoiceVersion = EngineApiVersions.Fcu.V5;
    }
}
