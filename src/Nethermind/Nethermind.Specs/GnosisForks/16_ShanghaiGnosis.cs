// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Specs.Forks;

namespace Nethermind.Specs.GnosisForks;

public class ShanghaiGnosis() : NamedGnosisReleaseSpec<ShanghaiGnosis>(Shanghai.Instance, LondonGnosis.Instance)
{
    public override void Apply(ReleaseSpec spec)
    {
        base.Apply(spec);
        spec.WithdrawalTimestamp = GnosisSpecProvider.ShanghaiTimestamp;
    }
}
