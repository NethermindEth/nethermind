// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Specs.Forks;

// BPO forks are blob-parameter-only forks chained off BPO2, parallel to Amsterdam
// (matching execution-spec-tests fork lineage); they must not inherit Amsterdam EIPs.
public class BPO3() : NamedReleaseSpec<BPO3>(BPO2.Instance)
{
    public override void Apply(NamedReleaseSpec spec)
    {
        spec.Name = "bpo3";
        spec.MaxBlobCount = 32;
        spec.TargetBlobCount = 21;
        spec.BlobBaseFeeUpdateFraction = 17805213;
    }
}
