// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using Nethermind.Consensus.Validators;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;

namespace Nethermind.EraE;


public class EraStoreFactory(
    ISpecProvider specProvider,
    IBlockValidator blockValidator,
    IFileSystem fileSystem,
    IEraEConfig eraConfig
) : Era1.EraStoreFactory(specProvider, blockValidator, fileSystem, new Era1.EraConfig { NetworkName = eraConfig.NetworkName, MaxEra1Size = eraConfig.MaxEraESize, Concurrency = eraConfig.Concurrency }), IEraStoreFactory
{
    private readonly ISpecProvider specProvider = specProvider;
    private readonly IBlockValidator blockValidator = blockValidator;
    private readonly IFileSystem fileSystem = fileSystem;

    public virtual Era1.IEraStore Create(string src, ISet<ValueHash256>? trustedAccumulators, ISet<ValueHash256>? trustedHistoricalRoots, IHistoricalSummariesProvider? historicalSummariesProvider)
    {
        return new EraStore(
            specProvider,
            blockValidator,
            fileSystem,
            eraConfig.NetworkName!,
            eraConfig.MaxEraESize,
            trustedAccumulators,
            trustedHistoricalRoots,
            historicalSummariesProvider,
            src,
            eraConfig.Concurrency);
    }
}

public interface IEraStoreFactory: Era1.IEraStoreFactory
{
    Era1.IEraStore Create(string src, ISet<ValueHash256>? trustedAccumulators, ISet<ValueHash256>? trustedHistoricalRoots, IHistoricalSummariesProvider? historicalSummariesProvider);
}

