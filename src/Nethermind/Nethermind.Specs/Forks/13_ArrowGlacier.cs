// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Specs.Forks;

public class ArrowGlacier() : NamedReleaseSpec<ArrowGlacier>(London.Instance)
{
    public override void Apply(NamedReleaseSpec spec)
    {
        spec.Name = "ArrowGlacier";
        spec.DifficultyBombDelay = 10700000L;
    }
}
