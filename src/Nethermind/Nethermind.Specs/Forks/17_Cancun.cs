// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Specs.Forks;

public class Cancun() : NamedReleaseSpec<Cancun>(Shanghai.Instance)
{
    public override void Apply(ReleaseSpec spec)
    {
        spec.Name = "Cancun";
        spec.IsEip1153Enabled = true;
        spec.IsEip4788Enabled = true;
        spec.IsEip4844Enabled = true;
        spec.IsEip5656Enabled = true;
        spec.IsEip6780Enabled = true;
        spec.Eip4788ContractAddress = Eip4788Constants.BeaconRootsAddress;
        spec.Eip4844TransitionTimestamp = MainnetSpecProvider.CancunBlockTimestamp;
        spec.MaxBlobCount = 6;
        spec.TargetBlobCount = 3;
        spec.BlobBaseFeeUpdateFraction = 3338477;
    }
}
