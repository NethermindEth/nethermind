// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Specs.Forks;

/// <summary>Frame transactions devnet fork: EIP-8141 over Osaka. Not scheduled on any public network.</summary>
public class Bogota() : NamedReleaseSpec<Bogota>(Osaka.Instance)
{
    public override void Apply(NamedReleaseSpec spec)
    {
        spec.Name = "bogota";
        spec.IsEip8141Enabled = true;
    }
}
