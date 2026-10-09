// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Specs.Forks;

/// <summary>
/// Prototype fork for EIP-8272 recent roots on top of EIP-8141 frame transactions. Not scheduled on any
/// network; exists so a devnet can activate them on their own, through <c>eip8272TransitionTimestamp</c>,
/// this label, or the Geth-genesis <c>eip8272PrototypeTime</c>.
/// </summary>
/// <remarks>
/// The label carries only EIP-8272; frame transactions themselves are scheduled by <see cref="Eip8141Prototype"/>.
/// </remarks>
public class Eip8272Prototype() : NamedReleaseSpec<Eip8272Prototype>(Eip8141Prototype.Instance)
{
    public override void Apply(NamedReleaseSpec spec)
    {
        spec.Name = "Eip8272Prototype";
        spec.IsEip8272Enabled = true;
    }
}
