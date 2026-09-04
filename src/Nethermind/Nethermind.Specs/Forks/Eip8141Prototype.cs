// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Specs.Forks;

/// <summary>
/// Prototype fork for EIP-8141 frame transactions. Not scheduled on any network; exists so a devnet
/// can activate frame transactions through <c>eip8141TransitionTimestamp</c>, this label, or the
/// Geth-genesis <c>eip8141PrototypeTime</c>, without joining a fork that means more. EIP-8141 composes
/// onto EIP-8037's gas dimension, so a chainspec must co-activate EIP-8037 (<c>amsterdamTime</c> on the
/// Geth side) no later than the prototype; <see cref="ChainSpecStyle.ChainSpecLoader"/> rejects any spec
/// that does not.
/// </summary>
public class Eip8141Prototype() : NamedReleaseSpec<Eip8141Prototype>(Amsterdam.Instance)
{
    public override void Apply(NamedReleaseSpec spec)
    {
        spec.Name = "Eip8141Prototype";
        spec.IsEip8141Enabled = true;
    }
}
