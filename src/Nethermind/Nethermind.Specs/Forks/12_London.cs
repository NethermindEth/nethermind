// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Specs.Forks;

public class London() : NamedReleaseSpec<London>(Berlin.Instance)
{
    public override void Apply(ReleaseSpec spec)
    {
        spec.Name = "London";
        spec.DifficultyBombDelay = 9700000L;
        spec.IsEip1559Enabled = true;
        spec.IsEip3198Enabled = true;
        spec.IsEip3529Enabled = true;
        spec.IsEip3541Enabled = true;
        spec.Eip1559TransitionBlock = 12965000;
    }
}
