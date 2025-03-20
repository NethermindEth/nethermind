// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core.Specs;
using Nethermind.Specs.Forks;

namespace Nethermind.Specs.GnosisForks;

public class LondonGnosis : London
{
    private static IReleaseSpec? _instance;

    private LondonGnosis()
    {
        SetGnosis(this);
    }

    public new static IReleaseSpec Instance => LazyInitializer.EnsureInitialized(ref _instance, static () => new LondonGnosis());

    public static void SetGnosis(London spec)
    {
        spec.FeeCollector = GnosisSpecProvider.FeeCollector;
    }
}
