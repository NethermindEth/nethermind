// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using Nethermind.Consensus.Validators;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.EraE.Config;
using Nethermind.EraE.Exceptions;
using Nethermind.EraE.Proofs;

namespace Nethermind.EraE.Store;

public class EraStoreFactory(
    ISpecProvider specProvider,
    IBlockValidator blockValidator,
    IFileSystem fileSystem,
    IEraEConfig eraConfig,
    Validator? validator = null,
    IRemoteEraClient? remoteClient = null
) : IEraStoreFactory
{
    public IEraStore Create(string src, ISet<ValueHash256>? trustedAccumulators)
    {
        IEraStore? localStore = null;
        try
        {
            localStore = new EraStore(
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
        catch (Exception e) when (remoteClient is not null && e is EraException or FileNotFoundException or DirectoryNotFoundException)
        {
            // No local era files — remote will supply them on demand.
        }

        if (remoteClient is null)
        {
            if (localStore is null)
                throw new EraException($"No eraE files found in '{src}' and no remote URL is configured.");
            return localStore;
        }

        string downloadDir = !string.IsNullOrWhiteSpace(eraConfig.RemoteDownloadDirectory)
            ? eraConfig.RemoteDownloadDirectory
            : src;

        return new RemoteEraStoreDecorator(localStore, remoteClient, downloadDir, eraConfig.MaxEraSize);
    }
}

public interface IEraStoreFactory
{
    IEraStore Create(string src, ISet<ValueHash256>? trustedAccumulators);
}
