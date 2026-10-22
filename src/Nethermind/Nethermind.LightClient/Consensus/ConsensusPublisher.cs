// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

namespace Nethermind.LightClient.Consensus;

internal sealed class ConsensusPublisher(
    Func<LightClientHeader> getFinalized,
    Func<LightClientHeader> getOptimistic,
    Func<LightClientHeader, CancellationToken, Task<(VerifiedHead Head, ulong Timestamp)>> resolve,
    Action<VerifiedHead, ulong> publishFinalized,
    TimeProvider clock)
{
    internal sealed record PublishedHeads(VerifiedHead? Finalized, VerifiedHead? Latest, ulong FinalizedTimestamp);

    private readonly Lock _commit = new();
    private PublishedHeads _heads = new(null, null, 0);

    internal PublishedHeads Heads => Volatile.Read(ref _heads);

    internal async Task RunAsync(Action<PublishedHeads> observe, Action<bool, Exception> reportFailure,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource workersStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task[] workers = [PublishLoopAsync(true, reportFailure, workersStop.Token),
            PublishLoopAsync(false, reportFailure, workersStop.Token), ObserveAsync(observe, workersStop.Token)];
        try { await Task.WhenAny(workers); }
        finally
        {
            await workersStop.CancelAsync();
            await Task.WhenAll(workers);
        }
    }

    private async Task PublishLoopAsync(bool finalized, Action<bool, Exception> reportFailure,
        CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (finalized) await PublishFinalizedAsync(cancellationToken);
                    else await PublishOptimisticAsync(cancellationToken);
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    reportFailure(finalized, exception);
                }
                await Task.Delay(TimeSpan.FromSeconds(1), clock, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task ObserveAsync(Action<PublishedHeads> observe, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                observe(Heads);
                await Task.Delay(TimeSpan.FromSeconds(1), clock, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    internal async Task PublishFinalizedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LightClientHeader header = getFinalized();
        Hash256 hash = ExecutionHash(header, "finalized");
        PublishedHeads current = Heads;
        ulong slot = header.Beacon!.Slot;
        if (current.Finalized is { } previous && slot <= previous.Slot) return;
        (VerifiedHead verified, ulong timestamp) = current.Finalized is { } finalized && finalized.BlockHash == hash
            ? (finalized with { Slot = slot }, current.FinalizedTimestamp)
            : await resolve(header, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        // Startup publication finishes before RunAsync; only its finalized worker owns this advertisement.
        publishFinalized(verified, timestamp);
        lock (_commit)
        {
            VerifiedHead? latest = _heads.Latest;
            if (latest is null || latest.Slot <= verified.Slot || latest.Number < verified.Number)
                latest = verified;
            Volatile.Write(ref _heads, new(verified, latest, timestamp));
        }
    }

    private async Task PublishOptimisticAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LightClientHeader header = getOptimistic();
        Hash256 hash = ExecutionHash(header, "optimistic");
        VerifiedHead? current = Heads.Latest;
        ulong slot = header.Beacon!.Slot;
        if (current is not null && slot <= current.Slot) return;
        VerifiedHead verified = current?.BlockHash == hash
            ? current with { Slot = slot }
            : (await resolve(header, cancellationToken)).Head;
        cancellationToken.ThrowIfCancellationRequested();
        lock (_commit)
        {
            PublishedHeads published = _heads;
            if (published.Latest is { } latest && verified.Slot <= latest.Slot) return;
            if (published.Finalized is { } finalized &&
                (verified.Slot < finalized.Slot || verified.Number < finalized.Number)) return;
            Volatile.Write(ref _heads, published with { Latest = verified });
        }
    }

    private static Hash256 ExecutionHash(LightClientHeader header, string kind) =>
        header.Execution?.BlockHash ?? header.ExecutionBlockHash
        ?? throw new InvalidDataException($"Verified {kind} header has no execution block hash.");
}
