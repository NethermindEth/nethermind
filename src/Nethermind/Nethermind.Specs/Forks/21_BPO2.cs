// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Specs.Forks;

public class BPO2() : NamedReleaseSpec<BPO2>(BPO1.Instance)
{
    public override void Apply(ReleaseSpec spec)
    {
        spec.Name = "bpo2";
        spec.MaxBlobCount = 21;
        spec.TargetBlobCount = 14;
        spec.BlobBaseFeeUpdateFraction = 11684671;
    }
}
