// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using Nethermind.Consensus.Validators;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;

namespace Nethermind.Era1;

public class EraStoreFactory(
    ISpecProvider specProvider,
    IBlockValidator blockValidator,
    IFileSystem fileSystem,
    IEraConfig eraConfig
) : IEraStoreFactory
{
    public IEraStore Create(string src, ISet<ValueHash256>? trustedAccumulators)
    {
        return new EraStore(
            specProvider,
            blockValidator,
            fileSystem,
            eraConfig.NetworkName!,
            eraConfig.MaxEra1Size,
            trustedAccumulators,
            src,
            eraConfig.Concurrency);
    }
}

public interface IEraStoreFactory
{
    IEraStore Create(string src, ISet<ValueHash256>? trustedAccumulators);
}
