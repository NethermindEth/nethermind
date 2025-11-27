// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Specs.GnosisForks;

public class BalancerGnosis : PragueGnosis
{
    private static IReleaseSpec _instance;

    private BalancerGnosis() : base()
    {
        Name = "Balancer";
    }

    public new static IReleaseSpec Instance => LazyInitializer.EnsureInitialized(ref _instance, static () => new BalancerGnosis());
}
