// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Specs.Forks;

namespace Nethermind.Specs.GnosisForks;

public class ShanghaiGnosis() : NamedGnosisReleaseSpec<ShanghaiGnosis>(Shanghai.Instance, LondonGnosis.Instance)
{
    public override void Apply(NamedReleaseSpec spec)
    {
        base.Apply(spec);
        spec.WithdrawalTimestamp = GnosisSpecProvider.ShanghaiTimestamp;
        // Gnosis transitioned to PoS at LondonGnosis → ShanghaiGnosis, skipping a Paris-equivalent
        // mainnet anchor whose Apply would otherwise have set this. Mark it explicitly here.
        spec.IsPostMerge = true;
    }
}
