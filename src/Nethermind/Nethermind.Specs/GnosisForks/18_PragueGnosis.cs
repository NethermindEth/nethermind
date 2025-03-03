// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core.Specs;

namespace Nethermind.Specs.GnosisForks;

public class PragueGnosis : Forks.Prague
{
    private static IReleaseSpec _instance;

    protected PragueGnosis() : base()
    {
        IsEip4844FeeCollectorEnabled = true;
        BlobBaseFeeUpdateFraction = 0x10fafa;
        TargetBlobCount = 1;
        MaxBlobCount = 2;
        FeeCollector = GnosisSpecProvider.FeeCollector;
    }

    public new static IReleaseSpec Instance => LazyInitializer.EnsureInitialized(ref _instance, static () => new PragueGnosis());
}
