// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Specs.Forks;

/// <summary>
/// Prototype fork for EIP-7906 transaction outcome assertions on top of EIP-8141 frame transactions. Not scheduled on any
/// network; exists so a devnet can activate them on their own, through <c>eip7906TransitionTimestamp</c>,
/// this label, or the Geth-genesis <c>eip7906PrototypeTime</c>.
/// </summary>
/// <remarks>
/// The label carries only EIP-7906; frame transactions themselves are scheduled by <see cref="Eip8141Prototype"/>.
/// </remarks>
public class Eip7906Prototype() : NamedReleaseSpec<Eip7906Prototype>(Eip8141Prototype.Instance)
{
    public override void Apply(NamedReleaseSpec spec)
    {
        spec.Name = "Eip7906Prototype";
        spec.IsEip7906Enabled = true;
    }
}
