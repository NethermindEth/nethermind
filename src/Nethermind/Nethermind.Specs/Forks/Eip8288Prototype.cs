// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Specs.Forks;

/// <summary>
/// Prototype fork for EIP-8288 (PQ signature and STARK aggregation), which extends EIP-8141. Enables
/// both flags because EIP-8288 requires EIP-8141. Not scheduled on any network; exists to gate the
/// prototype behind <c>IsEip8288Enabled</c>. Excluded from the Geth genesis fork mapping because it is
/// not a real fork name.
/// </summary>
public class Eip8288Prototype() : NamedReleaseSpec<Eip8288Prototype>(Amsterdam.Instance)
{
    public override void Apply(NamedReleaseSpec spec)
    {
        spec.Name = "Eip8288Prototype";
        spec.IsEip8141Enabled = true;
        spec.IsEip8288Enabled = true;
    }
}
