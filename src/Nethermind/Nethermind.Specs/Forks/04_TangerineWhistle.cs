// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Specs.Forks;

public class TangerineWhistle() : NamedReleaseSpec<TangerineWhistle>(Dao.Instance)
{
    public override void Apply(NamedReleaseSpec spec)
    {
        spec.Name = "Tangerine Whistle";
        spec.IsEip150Enabled = true;
    }
}
