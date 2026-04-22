// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Specs.Forks;

public class GrayGlacier() : NamedReleaseSpec<GrayGlacier>(ArrowGlacier.Instance)
{
    public override void Apply(ReleaseSpec spec)
    {
        spec.Name = "Gray Glacier";
        spec.DifficultyBombDelay = 11400000L;
    }
}
