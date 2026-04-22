// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Specs.Forks;

namespace Nethermind.Specs.GnosisForks;

public class LondonGnosis() : NamedGnosisReleaseSpec<LondonGnosis>(London.Instance)
{
    public override void Apply(ReleaseSpec spec)
    {
        base.Apply(spec);
        spec.FeeCollector = GnosisSpecProvider.FeeCollector;
    }
}
