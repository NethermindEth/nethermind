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
        IsEip8037Enabled = true;
        IsEip7778Enabled = true;
        IsEip7928Enabled = true;
        IsEip7708Enabled = true;
        IsEip8024Enabled = true;
        IsEip7843Enabled = true;
        IsEip7954Enabled = true;
        MaxCodeSize = CodeSizeConstants.MaxCodeSizeEip7954;
        Released = false;
    }

    public new static IReleaseSpec Instance => LazyInitializer.EnsureInitialized(ref _instance, static () => new Amsterdam());
    public static IReleaseSpec NoEip8037Instance => LazyInitializer.EnsureInitialized(ref _noEip8037Instance, static () => new Amsterdam { IsEip8037Enabled = false });
}
