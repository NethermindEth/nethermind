// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Specs.Forks;

public class Prague() : NamedReleaseSpec<Prague>(Cancun.Instance)
{
    public override void Apply(ReleaseSpec spec)
    {
        spec.Name = "Prague";
        spec.IsEip2537Enabled = true;
        spec.IsEip2935Enabled = true;
        spec.IsEip7702Enabled = true;
        spec.IsEip6110Enabled = true;
        spec.IsEip7002Enabled = true;
        spec.IsEip7251Enabled = true;
        spec.IsEip7623Enabled = true;
        spec.Eip2935ContractAddress = Eip2935Constants.BlockHashHistoryAddress;
        spec.MaxBlobCount = 9;
        spec.TargetBlobCount = 6;
        spec.BlobBaseFeeUpdateFraction = 5007716;
        spec.DepositContractAddress = Eip6110Constants.MainnetDepositContractAddress;
    }
}
