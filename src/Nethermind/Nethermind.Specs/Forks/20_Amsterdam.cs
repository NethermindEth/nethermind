// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core.Specs;

namespace Nethermind.Specs.Forks;

public class Amsterdam: Osaka {

    private static IReleaseSpec _instance;

    protected Amsterdam()
    {
        Name = "Amsterdam";
        IsEip4762Enabled = true;
        IsEip6800Enabled = true;
    }
    public new static IReleaseSpec Instance => LazyInitializer.EnsureInitialized(ref _instance, static () => new Amsterdam());
}
