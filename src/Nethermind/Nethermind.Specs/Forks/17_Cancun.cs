// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Specs.Forks;

public class Cancun : Shanghai
{
    private static IReleaseSpec _instance;

    protected Cancun()
    {
        Name = "Cancun";
        IsEip1153Enabled = false;
        IsEip4788Enabled = false;
        IsEip4844Enabled = false;
        IsEip5656Enabled = false;
        IsEip6780Enabled = true;
        Eip4788ContractAddress = Eip4788Constants.BeaconRootsAddress;
    }

    public new static IReleaseSpec Instance => LazyInitializer.EnsureInitialized(ref _instance, () => new Cancun());
}
