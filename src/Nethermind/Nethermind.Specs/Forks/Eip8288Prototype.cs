// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Specs.Forks;

/// <summary>
/// Prototype fork for EIP-8288 aggregated dependency proofs, which extends EIP-8141. Not scheduled on any network; exists
/// so a devnet can activate aggregation through <c>eip8288TransitionTimestamp</c>, this label, or the Geth-genesis
/// <c>eip8288PrototypeTime</c>.
/// </summary>
public class Eip8288Prototype() : NamedReleaseSpec<Eip8288Prototype>(Eip8141Prototype.Instance)
{
    public override void Apply(NamedReleaseSpec spec)
    {
        spec.Name = "Eip8288Prototype";
        spec.IsEip8288Enabled = true;
    }
}
