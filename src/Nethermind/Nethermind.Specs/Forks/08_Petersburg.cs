// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Specs.Forks;

public class Petersburg() : NamedReleaseSpec<Petersburg>(Constantinople.Instance)
{
    public override void Apply(NamedReleaseSpec spec)
    {
        spec.Name = "Petersburg";
        spec.IsEip1283Enabled = false;
    }
}
