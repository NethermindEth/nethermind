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
    IEraConfig eraConfig
) : Era1.EraStoreFactory(
    specProvider,
    blockValidator, 
    fileSystem, 
    new Era1.EraConfig { 
        MaxEra1Size = eraConfig.MaxEraESize, 
        NetworkName = eraConfig.NetworkName, 
        Concurrency = eraConfig.Concurrency, 
        ImportBlocksBufferSize = eraConfig.ImportBlocksBufferSize, 
        TrustedAccumulatorFile = eraConfig.TrustedAccumulatorFile, 
        From = eraConfig.From, 
        To = eraConfig.To, 
        ImportDirectory = eraConfig.ImportDirectory 
    }
) {
    public override Era1.IEraStore Create(string src, ISet<ValueHash256>? trustedAccumulators)
    {
        return new EraStore(
            specProvider,
            blockValidator,
            fileSystem,
            eraConfig.NetworkName!,
            eraConfig.MaxEraESize,
            trustedAccumulators,
            src,
            eraConfig.Concurrency);
    }
}

