// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Specs.Forks;

public class Shanghai() : NamedReleaseSpec<Shanghai>(Paris.Instance)
{
    public override void Apply(ReleaseSpec spec)
    {
        spec.Name = "Shanghai";
        spec.IsEip3651Enabled = true;
        spec.IsEip3855Enabled = true;
        spec.IsEip3860Enabled = true;
        spec.IsEip4895Enabled = true;
        spec.WithdrawalTimestamp = MainnetSpecProvider.ShanghaiBlockTimestamp;
    }
}
