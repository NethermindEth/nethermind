// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Specs.Forks;

public class Amsterdam : BPO5
{
    private static IReleaseSpec _instance;
    private static IReleaseSpec _noEip8037Instance;

    public Amsterdam()
    {
        Name = "Amsterdam";
        // ePBS-devnet-0: disable all bal-devnet-2 EIPs regardless of chainspec config
        IsEip8037Enabled = false;
        IsEip7778Enabled = false;
        IsEip7928Enabled = false;
        IsEip7708Enabled = false;
        IsEip8024Enabled = false;
        IsEip7843Enabled = false;
        IsEip7954Enabled = false;
        Released = false;
    }

    public new static IReleaseSpec Instance => LazyInitializer.EnsureInitialized(ref _instance, static () => new Amsterdam());
    public static IReleaseSpec NoEip8037Instance => LazyInitializer.EnsureInitialized(ref _noEip8037Instance, static () => new Amsterdam { IsEip8037Enabled = false });
}
