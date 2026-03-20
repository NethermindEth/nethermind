// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Specs.Forks;

public class Istanbul() : NamedReleaseSpec<Istanbul>(ConstantinopleFix.Instance)
{
    public override void Apply(ReleaseSpec spec)
    {
        spec.Name = "Istanbul";
        spec.IsEip1344Enabled = true;
        spec.IsEip2028Enabled = true;
        spec.IsEip152Enabled = true;
        spec.IsEip1108Enabled = true;
        spec.IsEip1884Enabled = true;
        spec.IsEip2200Enabled = true;
    }
}
