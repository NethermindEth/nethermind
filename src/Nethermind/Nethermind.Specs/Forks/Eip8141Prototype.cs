// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Specs.Forks;

/// <summary>
/// Prototype fork for EIP-8141 frame transactions. Not scheduled on any network; exists so a devnet
/// can activate frame transactions on their own, through <c>eip8141TransitionTimestamp</c>, this
/// label, or the Geth-genesis <c>eip8141PrototypeTime</c>, without joining a fork that means more.
/// </summary>
public class Eip8141Prototype() : NamedReleaseSpec<Eip8141Prototype>(Amsterdam.Instance)
{
    public override void Apply(NamedReleaseSpec spec)
    {
        spec.Name = "Eip8141Prototype";
        spec.IsEip8141Enabled = true;
    }
}
