// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core.Specs;
using Nethermind.Specs.Forks;

namespace Nethermind.Specs.GnosisForks;

public class ShanghaiGnosis : Shanghai
{
    private static IReleaseSpec? _instance;

    private ShanghaiGnosis()
    {
        ToGnosisFork(this);
    }

    public static void ToGnosisFork(Shanghai spec)
    {
        LondonGnosis.ToGnosisFork(spec);
    }

    public new static IReleaseSpec Instance => LazyInitializer.EnsureInitialized(ref _instance, static () => new ShanghaiGnosis());
}
