// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Specs.Forks;

public class BPO1() : NamedReleaseSpec<BPO1>(Osaka.Instance)
{
    public override void Apply(ReleaseSpec spec)
    {
        spec.Name = "bpo1";
        spec.MaxBlobCount = 15;
        spec.TargetBlobCount = 10;
        spec.BlobBaseFeeUpdateFraction = 8346193;
    }
}
