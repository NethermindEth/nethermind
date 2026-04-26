// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Specs.Forks;

public class BPO5() : NamedReleaseSpec<BPO5>(BPO4.Instance)
{
    public override void Apply(ReleaseSpec spec)
    {
        spec.Name = "bpo5";
        spec.MaxBlobCount = 72;
        spec.TargetBlobCount = 48;
        spec.BlobBaseFeeUpdateFraction = 40061729;
    }
}
