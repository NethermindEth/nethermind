// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Specs.Forks;

public class Prague : Cancun
{
    private static IReleaseSpec _instance;

    public Prague()
    {
        Name = "Prague";
        IsEip2537Enabled = true;
        IsEip2935Enabled = true;
        IsEip7702Enabled = true;
        IsEip6110Enabled = true;
        IsEip7002Enabled = true;
        IsEip7251Enabled = true;
        IsEip7623Enabled = true;
        Eip2935ContractAddress = Eip2935Constants.BlockHashHistoryAddress;
        MaxBlobCount = 9;
        TargetBlobCount = 6;
        BlobBaseFeeUpdateFraction = 5007716;
        DepositContractAddress = Eip6110Constants.MainnetDepositContractAddress;
    }

    public new static IReleaseSpec Instance => LazyInitializer.EnsureInitialized(ref _instance, static () => new Prague());
}
