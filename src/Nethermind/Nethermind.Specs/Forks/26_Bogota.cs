// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Specs.Forks;

/// <summary>Fork enabling EIP-7805 inclusion lists on top of Amsterdam.</summary>
/// <remarks>
/// EIP-8141 frame transactions are deliberately not bundled here. Activating them installs the frame-tx
/// expiry-verifier predeploy, which adds a code change to every block's EIP-7928 access list and so shifts
/// the access-list hash the Bogota consensus fixtures pin. A chain wanting both schedules
/// <c>eip8141TransitionTimestamp</c> alongside this fork.
/// </remarks>
public class Bogota() : NamedReleaseSpec<Bogota>(Amsterdam.Instance)
{
    public override void Apply(NamedReleaseSpec spec)
    {
        spec.Name = "Bogota";
        spec.IsEip7805Enabled = true;
        spec.EngineApiNewPayloadVersion = EngineApiVersions.NewPayload.V6;
        spec.EngineApiForkchoiceVersion = EngineApiVersions.Fcu.V5;
    }
}
