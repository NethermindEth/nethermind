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

    public EraStore(
        ISpecProvider specProvider,
        IBlockValidator blockValidator,
        IFileSystem fileSystem,
        string networkName,
        int maxEraSize,
        ISet<ValueHash256>? trustedAcccumulators,
        ISet<ValueHash256>? trustedHistoricalRoots,
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
                // fix
                ValueHash256 eraAccumulator = await reader.VerifyContent(_specProvider, _blockValidator, _verifyConcurrency, cancellation);
                if (_trustedAccumulators != null && !_trustedAccumulators.Contains(eraAccumulator))
                {
                    throw new EraVerificationException($"Unable to verify epoch {epoch}. Accumulator {eraAccumulator} not trusted");
                }
            });

            await Task.WhenAll(checksumTask, accumulatorTask);

            _verifiedEpochs.TryAdd(epoch, true);
        }
    }
}
