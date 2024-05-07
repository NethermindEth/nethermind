// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Specs.Forks;

public class Prague : Cancun
{
    private static IReleaseSpec _instance;

    protected Prague()
    {
        Name = "Prague";
        IsEip2537Enabled = true;
        IsEip2935Enabled = true;
        Eip2935ContractAddress = Eip2935Constants.BlockHashHistoryAddress;
    }

    public new static IReleaseSpec Instance => LazyInitializer.EnsureInitialized(ref _instance, () => new Prague());
}
