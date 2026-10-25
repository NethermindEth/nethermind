// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Specs.Forks;

/// <summary>
/// Prototype fork for EIP-8250 keyed nonces on top of EIP-8141 frame transactions. Not scheduled on any
/// network; exists so a devnet can activate them on their own, through <c>eip8250TransitionTimestamp</c>,
/// this label, or the Geth-genesis <c>eip8250PrototypeTime</c>.
/// </summary>
/// <remarks>
/// The label carries only EIP-8250; frame transactions themselves are scheduled by <see cref="Eip8141Prototype"/>.
/// </remarks>
public class Eip8250Prototype() : NamedReleaseSpec<Eip8250Prototype>(Eip8141Prototype.Instance)
{
    public override void Apply(NamedReleaseSpec spec)
    {
        spec.Name = "Eip8250Prototype";
        spec.IsEip8250Enabled = true;
    }
}
