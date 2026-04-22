// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Specs.Forks;

namespace Nethermind.Specs.GnosisForks;

public class CancunGnosis() : NamedGnosisReleaseSpec<CancunGnosis>(Cancun.Instance, ShanghaiGnosis.Instance)
{
    public override void Apply(ReleaseSpec spec)
    {
        base.Apply(spec);
        spec.Eip4844TransitionTimestamp = GnosisSpecProvider.CancunTimestamp;
        spec.MaxBlobCount = 2;
        spec.TargetBlobCount = 1;
        spec.BlobBaseFeeUpdateFraction = 1112826;
    }
}
