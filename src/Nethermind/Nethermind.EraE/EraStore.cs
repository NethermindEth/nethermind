// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using Nethermind.Consensus.Validators;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Era1.Exceptions;

namespace Nethermind.EraE;
public class EraStore: Era1.EraStore {
    private readonly ISet<ValueHash256>? _trustedHistoricalRoots;
    private readonly IHistoricalSummariesProvider? _historicalSummariesProvider;

    public EraStore(
        ISpecProvider specProvider,
        IBlockValidator blockValidator,
        IFileSystem fileSystem,
        string networkName,
        int maxEraSize,
        ISet<ValueHash256>? trustedAcccumulators,
        ISet<ValueHash256>? trustedHistoricalRoots,
        IHistoricalSummariesProvider? historicalSummariesProvider,
        string directory,
        int verifyConcurrency = 0,
        string checksumsFileName = EraExporter.ChecksumsFileName
    ) : base(
        specProvider, 
        blockValidator, 
        fileSystem, 
        networkName, 
        maxEraSize, 
        trustedAcccumulators, 
        directory,
        verifyConcurrency,
        checksumsFileName
    ) {
        _trustedHistoricalRoots = trustedHistoricalRoots;
        _historicalSummariesProvider = historicalSummariesProvider;
    }

    protected override EraReader GetReader(long epoch)
    {
        GuardMissingEpoch(epoch);
        return new EraReader(new E2StoreReader(_epochs[epoch]), _historicalSummariesProvider, _trustedHistoricalRoots, _trustedAccumulators);
    }

    protected async ValueTask EnsureEpochVerified(long epoch, EraReader reader, CancellationToken cancellation)
    {
        if (!(_verifiedEpochs.TryGetValue(epoch, out bool verified) && verified))
        {
            Task checksumTask = Task.Run(() =>
            {
                ValueHash256 checksum = reader.CalculateChecksum();
                ValueHash256 expectedChecksum = _checksums[epoch - FirstEpoch];
                if (checksum != expectedChecksum)
                {
                    throw new EraVerificationException(
                        $"Checksum verification failed. Checksum: {checksum}, Expected: {expectedChecksum}");
                }
            });

            Task accumulatorTask = Task.Run(async () =>
            {
                await reader.VerifyContent(_specProvider, _blockValidator, _verifyConcurrency, cancellation);
            });

            await Task.WhenAll(checksumTask, accumulatorTask);

            _verifiedEpochs.TryAdd(epoch, true);
        }
    }
}
