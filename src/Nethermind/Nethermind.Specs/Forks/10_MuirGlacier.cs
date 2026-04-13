// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Specs.Forks;

public class MuirGlacier() : NamedReleaseSpec<MuirGlacier>(Istanbul.Instance)
{
    public override void Apply(ReleaseSpec spec)
    {
        spec.Name = "Muir Glacier";
        spec.DifficultyBombDelay = 9000000L;
    }
}
