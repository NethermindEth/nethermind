// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Specs.Forks;

public class Paris() : NamedReleaseSpec<Paris>(GrayGlacier.Instance)
{
    public override void Apply(ReleaseSpec spec)
    {
        spec.Name = "Paris";
    }
}
