// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Specs.Forks;

public class Berlin() : NamedReleaseSpec<Berlin>(MuirGlacier.Instance)
{
    public override void Apply(NamedReleaseSpec spec)
    {
        spec.Name = "Berlin";
        spec.IsEip2565Enabled = true;
        spec.IsEip2929Enabled = true;
        spec.IsEip2930Enabled = true;
    }
}
