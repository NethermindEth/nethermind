// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Specs.Forks;

/// <summary>
/// Prototype fork for EIP-8288 (PQ signature and STARK aggregation), which extends EIP-8141. Built on
/// <see cref="Bogota"/> (Osaka + EIP-8141) to match the frame-transactions devnet base, adding
/// <c>IsEip8288Enabled</c>. Not scheduled on any network; excluded from the Geth genesis fork mapping
/// because it is not a real fork name.
/// </summary>
public class Eip8288Prototype() : NamedReleaseSpec<Eip8288Prototype>(Bogota.Instance)
{
    public override void Apply(NamedReleaseSpec spec)
    {
        spec.Name = "Eip8288Prototype";
        spec.IsEip8141Enabled = true;
        spec.IsEip8288Enabled = true;
    }
}
