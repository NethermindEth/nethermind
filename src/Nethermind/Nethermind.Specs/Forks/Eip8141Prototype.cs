// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Specs.Forks;

/// <summary>EIP-8141 over Amsterdam. Not a real fork name, so it is excluded from the genesis fork mapping.</summary>
public class Eip8141Prototype() : NamedReleaseSpec<Eip8141Prototype>(Amsterdam.Instance)
{
    public override void Apply(NamedReleaseSpec spec)
    {
        spec.Name = "Eip8141Prototype";
        spec.IsEip8141Enabled = true;
    }
}
