// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Specs.Forks;

public class Osaka() : NamedReleaseSpec<Osaka>(Prague.Instance)
{
    public override void Apply(NamedReleaseSpec spec)
    {
        spec.Name = "Osaka";
        spec.IsEip7594Enabled = true;
        spec.IsEip7823Enabled = true;
        spec.IsEip7825Enabled = true;
        spec.IsEip7883Enabled = true;
        spec.IsEip7918Enabled = true;
        spec.IsEip7934Enabled = true;
        spec.IsEip7939Enabled = true;
        spec.IsEip7951Enabled = true;
    }
}
