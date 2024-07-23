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
        IsVerkleTreeEipEnabled = true;
        IsEip2935Enabled = true;
        IsEip6780Enabled = true;
        IsEip1153Enabled = false;
        IsEip4788Enabled = false;
        IsEip4844Enabled = false;
        IsEip5656Enabled = false;
    }

    public static new IReleaseSpec Instance => LazyInitializer.EnsureInitialized(ref _instance, () => new Prague());

}
