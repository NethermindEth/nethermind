// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core.Specs;

namespace Nethermind.Specs.Forks;

public class Amsterdam : BPO5
{
    private static IReleaseSpec _instance;

    public Amsterdam()
    {
        Name = "Amsterdam";
        IsEip7778Enabled = true;
        IsEip7928Enabled = true;
        IsEip7708Enabled = true;
        IsEip8024Enabled = true;
        IsEip7843Enabled = true;
        Released = false;
    }

    public new static IReleaseSpec Instance => LazyInitializer.EnsureInitialized(ref _instance, static () => new Amsterdam());
}
