// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Specs.Forks;

/// <summary>
/// Prototype fork for EIP-8369 Profile 2 enforcement: <see cref="Bogota"/>'s EIP-7805 inclusion lists with
/// EIP-8141 frame transactions, EIP-8250 keyed nonces and EIP-8272 recent roots. Not scheduled on any network;
/// exists so fixtures whose <c>Bogota</c> combines all four can be run through <c>--forkAlias Bogota=Eip8369Prototype</c>.
/// </summary>
public class Eip8369Prototype() : NamedReleaseSpec<Eip8369Prototype>(Bogota.Instance)
{
    public override void Apply(NamedReleaseSpec spec)
    {
        spec.Name = "Eip8369Prototype";
        spec.IsEip8141Enabled = true;
        spec.IsEip8250Enabled = true;
        spec.IsEip8272Enabled = true;
    }
}
