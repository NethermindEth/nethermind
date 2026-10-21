// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Specs.Forks;

public class BPO4() : NamedReleaseSpec<BPO4>(BPO3.Instance)
{
    public override void Apply(NamedReleaseSpec spec)
    {
        spec.Name = "bpo4";
        spec.MaxBlobCount = 48;
        spec.TargetBlobCount = 32;
        spec.BlobBaseFeeUpdateFraction = 26707819;
    }
}
