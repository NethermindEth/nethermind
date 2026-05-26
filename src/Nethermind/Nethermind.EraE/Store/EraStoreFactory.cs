// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using Nethermind.Consensus.Validators;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.EraE.Config;
using EraException = Nethermind.Era1.EraException;
using Nethermind.EraE.Proofs;
using Nethermind.Logging;

namespace Nethermind.EraE.Store;

public sealed class EraStoreFactory(
    ISpecProvider specProvider,
    IBlockValidator blockValidator,
    IFileSystem fileSystem,
    IEraEConfig eraConfig,
    ILogManager logManager,
    Validator? validator = null,
    IRemoteEraClient? remoteClient = null
) : IEraStoreFactory
{
    private readonly ILogger _logger = logManager.GetClassLogger<EraStoreFactory>();

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
            if (_logger.IsDebug) _logger.Debug($"No local EraE files found in '{src}': {e.Message}. Remote will supply them on demand.");
        }

        if (remoteClient is null)
        {
            return localStore ?? throw new EraException($"No eraE files found in '{src}' and no remote URL is configured.");
        }

        string downloadDir = !string.IsNullOrWhiteSpace(eraConfig.RemoteDownloadDirectory)
            ? eraConfig.RemoteDownloadDirectory
            : src;

        fileSystem.Directory.CreateDirectory(downloadDir);
        return new RemoteEraStoreDecorator(localStore, remoteClient, downloadDir, eraConfig.MaxEraSize);
    }
}
