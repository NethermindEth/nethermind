// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using Nethermind.Consensus.Validators;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.EraE.Config;
using Nethermind.EraE.Proofs;

namespace Nethermind.EraE.Store;

public class EraStoreFactory(
    ISpecProvider specProvider,
    IBlockValidator blockValidator,
    IFileSystem fileSystem,
    IEraEConfig eraConfig,
    Validator? validator = null
) : IEraStoreFactory
{
    public IEraStore Create(string src, ISet<ValueHash256>? trustedAccumulators) =>
        new EraStore(
            specProvider,
            blockValidator,
            fileSystem,
            eraConfig.NetworkName!,
            eraConfig.MaxEraSize,
            trustedAccumulators,
            src,
            eraConfig.Concurrency,
            validator);
}

public interface IEraStoreFactory
{
    IEraStore Create(string src, ISet<ValueHash256>? trustedAccumulators);
}
