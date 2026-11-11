// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using DotNetty.Buffers;
using Microsoft.Extensions.Logging;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.LightClient.Consensus;
using Nethermind.LightClient.Rpc;
using Nethermind.Logging;
using Nethermind.Logging.Microsoft;
using Nethermind.Network;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Nethermind.LightClient.Mobile;

/// <summary>Runs mainnet light-client verification and follows an account's ETH and selected token balances over P2P.</summary>
public static class MobileClient
{
    public const string CheckpointUrl = "https://mainnet.checkpoint.sigp.io";
    private const long CacheCapacity = 4 * 1024 * 1024;

    /// <summary>Runs until cancellation, joining network and verification workers before returning.</summary>
    /// <remarks>The HTTPS checkpoint provider is the initial trust anchor. All subsequent headers and state come from P2P and are verified locally.</remarks>
    public static async Task RunAsync(string dataDirectory, MobileTrackingSettings settings, Action<MobileProgress> report, ILogManager logs,
        CancellationToken cancellationToken)
    {
        if (!MobileTrackingSettings.TryCreate(settings.AccountAddress, settings.Usdt, settings.Dai, settings.Usdc, out MobileTrackingSettings? validated))
            throw new ArgumentException("Tracking requires a valid Ethereum account address.", nameof(settings));
        settings = validated;
        Address account = new(settings.AccountAddress);
        MobileToken[] selectedTokens = settings.SelectedTokens();
        ConfigureNetworkBuffers();
        Directory.CreateDirectory(dataDirectory);
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken token = stop.Token;
        using NethermindLoggerFactory factory = new(logs);
        ILogger logger = factory.CreateLogger("LightClient.Mobile");
        BeaconChainSpec spec = BeaconChainSpec.Mainnet;
        TimeProvider clock = TimeProvider.System;
        ulong CurrentSlot() => spec.GetSlotAtTime((ulong)clock.GetUtcNow().ToUnixTimeSeconds());
        bool Fresh(VerifiedHead? head) => head?.IsFresh(CurrentSlot(), 120 / spec.SecondsPerSlot) ?? false;
        report(new("Selecting checkpoint", Settings: settings));
        using HttpClientHandler handler = new() { AllowAutoRedirect = false };
        using HttpClient http = new(handler) { Timeout = TimeSpan.FromSeconds(10) };
        CheckpointSelection selected = await CheckpointSource.ResolveAsync(http, CheckpointUrl, "mainnet",
            dataDirectory, spec, CurrentSlot(), null, token);
        Hash256 checkpoint = selected.Root;
        logger.LogInformation("Mainnet checkpoint {Checkpoint}, provider {Provider}", checkpoint, selected.Provider);

        using SemaphoreSlim gate = new(1, 1);
        // A new key accompanies a new ENR sequence; consensus provenance and journals remain durable.
        using MemColumnsDb<BeaconChainDbColumns> beaconDb = new();
        using IFullDb peersDb = BoundedEnrStorage.OpenDb(Path.Combine(dataDirectory, "cl", "1", "discovery"), logs, beacon: true);
        await using BeaconPeerTransport beacon = new(spec, logs, beaconDb,
            new BoundedEnrStorage(new NetworkStorage(peersDb, logs)), time: clock, quicPort: 0);
        report(new("Discovering beacon peers", Settings: settings));
        await beacon.StartAsync(token);
        VerifiedConsensusJournal journal = new(dataDirectory, spec, "mainnet", checkpoint, logger);
        report(new("Verifying checkpoint", BeaconPeers: beacon.PeerCount, Settings: settings));
        (LightClientStore store, _) = await ConsensusSync.BootstrapAsync(spec, checkpoint, journal,
            beacon.BootstrapAsync, clock, token);
        LightClientHeader finalized = store.FinalizedHeader;
        LightClientHeader optimistic = store.OptimisticHeader;
        beacon.PublishVerifiedHeads(finalized, optimistic, checkpoint);
        beacon.NeedsCommitteeSupplier = !store.NextSyncCommitteeKnown;
        // Gloas authenticates an execution hash first; advertise canonical genesis until its header is available.
        VerifiedHead initial = finalized.Execution is { } payload
            ? new(finalized.Beacon!.Slot, payload.BlockNumber, payload.BlockHash!, payload.StateRoot!)
            : new(0, 0, KnownHashes.MainnetGenesis, new Hash256("0xd7f8974fb5ac78d9ac099b9ad5018bedc2ce0a72dad1827a1709da30580f0544"));
        ulong Timestamp(VerifiedHead head) => spec.GenesisTime + head.Slot * spec.SecondsPerSlot;

        report(new("Discovering execution peers", BeaconPeers: beacon.PeerCount, Settings: settings));
        await using ExecutionPeerTransport execution = new("mainnet", logs, initial,
            initial.Number == 0 ? 0 : Timestamp(initial), dataDirectory: dataDirectory, cacheCapacity: CacheCapacity);
        await execution.StartAsync(token);
        ConsensusPublisher publisher = new(() => Volatile.Read(ref finalized), () => Volatile.Read(ref optimistic),
            ResolveAsync, execution.UpdateHead, clock);
        ConsensusSync.Processor consensus = new(store, journal, spec, clock, gate, Publish, stop.Cancel, logger, token);
        Channel<bool> refresh = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropWrite,
        });
        MobileBalance? balance = null;
        string? balanceError = null;
        MobileTokenBalance[] tokenBalances = new MobileTokenBalance[selectedTokens.Length];
        for (int i = 0; i < selectedTokens.Length; i++)
            tokenBalances[i] = new(selectedTokens[i].Symbol, selectedTokens[i].Decimals);
        using VerifiedCall? calls = selectedTokens.Length == 0 ? null : new(execution, execution.SpecProvider, logs, clock, CacheCapacity);
        beacon.StartGossip(consensus.ProcessAsync, consensus.ProcessAsync, token);
        List<Task> workers =
        [
            ConsensusSync.RunRepairLoopsAsync(RepairAsync, PollOptimisticAsync,
                () => Volatile.Read(ref optimistic).Beacon!.Slot, CurrentSlot,
                TimeSpan.FromSeconds(spec.SecondsPerSlot), clock, token, refresh.Reader),
            publisher.RunAsync(Observe, (_, error) => logger.LogWarning("Waiting for execution head: {Reason}", error.Message), token),
            FollowBalanceAsync(),
            beacon.GossipCompletion,
        ];
        for (int i = 0; i < selectedTokens.Length; i++) workers.Add(FollowTokenAsync(i));
        try { await await Task.WhenAny(workers); }
        finally
        {
            await stop.CancelAsync();
            await Task.WhenAll(workers).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        cancellationToken.ThrowIfCancellationRequested();
        throw new IOException("Light-client verification stopped. Restart after checking the displayed error and device clock.");

        async Task<(VerifiedHead Head, ulong Timestamp)> ResolveAsync(LightClientHeader header, CancellationToken requestToken)
        {
            if (header.Execution is { } authenticated)
            {
                VerifiedHead head = new(header.Beacon!.Slot, authenticated.BlockNumber, authenticated.BlockHash!, authenticated.StateRoot!);
                return (head, Timestamp(head));
            }
            Hash256 hash = header.ExecutionBlockHash ?? throw new InvalidDataException("Authenticated header has no execution block hash.");
            BlockHeader block = await execution.GetHeaderByHashAsync(hash, requestToken);
            return (new(header.Beacon!.Slot, block.Number, hash, block.StateRoot!), block.Timestamp);
        }

        void Publish()
        {
            LightClientHeader latestFinalized = store.FinalizedHeader;
            LightClientHeader latestOptimistic = store.OptimisticHeader;
            Volatile.Write(ref finalized, latestFinalized);
            Volatile.Write(ref optimistic, latestOptimistic);
            beacon.PublishVerifiedHeads(latestFinalized, latestOptimistic, checkpoint);
            beacon.NeedsCommitteeSupplier = !store.NextSyncCommitteeKnown;
        }

        void Observe(ConsensusPublisher.PublishedHeads heads)
        {
            MobileBalance? observed = Volatile.Read(ref balance);
            MobileTokenBalance[] observedTokens = new MobileTokenBalance[tokenBalances.Length];
            for (int i = 0; i < tokenBalances.Length; i++) observedTokens[i] = Volatile.Read(ref tokenBalances[i]);
            bool fresh = Fresh(heads.Latest);
            report(new("Following mainnet", heads.Latest?.Number, heads.Latest?.Slot, heads.Finalized?.Number,
                fresh, beacon.PeerCount, execution.PeerCount, execution.SnapPeerCount, observed,
                fresh && observed?.BlockHash == heads.Latest?.BlockHash.ToString(), Volatile.Read(ref balanceError),
                Array.AsReadOnly(observedTokens), settings));
        }

        async Task FollowBalanceAsync()
        {
            while (!token.IsCancellationRequested)
            {
                VerifiedHead? head = publisher.Heads.Latest;
                if (Fresh(head) && Volatile.Read(ref balance)?.BlockHash != head!.BlockHash.ToString())
                {
                    long started = Stopwatch.GetTimestamp();
                    try
                    {
                        using CancellationTokenSource request = CancellationTokenSource.CreateLinkedTokenSource(token);
                        request.CancelAfter(TimeSpan.FromSeconds(30));
                        Account state = await execution.GetAccountAsync(head, account, request.Token);
                        MobileBalance verified = new(state.Balance.ToString(null, CultureInfo.InvariantCulture), head.Number,
                            head.BlockHash.ToString(), head.StateRoot.ToString(), clock.GetUtcNow(),
                            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                        Volatile.Write(ref balance, verified);
                        Volatile.Write(ref balanceError, null);
                        logger.LogInformation("Verified balance {Wei} wei at block {Block}, SNAP request {Elapsed:F1} ms",
                            verified.Wei, verified.BlockNumber, verified.RequestMilliseconds);
                    }
                    catch (Exception error) when (!token.IsCancellationRequested &&
                        error is IOException or TimeoutException or OperationCanceledException)
                    {
                        Volatile.Write(ref balanceError, error.Message);
                        logger.LogWarning("Balance unavailable: {Reason}", error.Message);
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(3), clock, token);
            }
        }

        async Task FollowTokenAsync(int index)
        {
            MobileToken asset = selectedTokens[index];
            MobileTokenReader tokenReader = new(calls!, account, clock);
            while (!token.IsCancellationRequested)
            {
                VerifiedHead? head = publisher.Heads.Latest;
                if (Fresh(head) && Volatile.Read(ref tokenBalances[index]).BlockHash != head!.BlockHash.ToString())
                {
                    try
                    {
                        using CancellationTokenSource request = CancellationTokenSource.CreateLinkedTokenSource(token);
                        request.CancelAfter(TimeSpan.FromSeconds(30));
                        MobileTokenBalance verified = await tokenReader.ReadAsync(asset, head, request.Token);
                        Volatile.Write(ref tokenBalances[index], verified);
                        logger.LogInformation("Verified {Symbol} balance {Units} units at block {Block}, local call {Elapsed:F1} ms",
                            asset.Symbol, verified.Units, verified.BlockNumber, verified.RequestMilliseconds);
                    }
                    catch (Exception error) when (!token.IsCancellationRequested &&
                        error is IOException or InvalidDataException or TimeoutException or OperationCanceledException or RpcException)
                    {
                        Volatile.Write(ref tokenBalances[index], tokenBalances[index] with { Error = error.Message });
                        logger.LogWarning("{Symbol} balance unavailable: {Reason}", asset.Symbol, error.Message);
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(3), clock, token);
            }
        }

        async Task RepairAsync(CancellationToken requestToken)
        {
            try
            {
                await ConsensusSync.CatchUpAsync(store, spec, beacon.UpdateAsync, CurrentSlot,
                    consensus.ProcessAsync, requestToken, clock);
                await consensus.ForceUpdateAsync(requestToken);
                using CancellationTokenSource attempt = CancellationTokenSource.CreateLinkedTokenSource(requestToken);
                attempt.CancelAfter(TimeSpan.FromSeconds(15));
                await beacon.PollFinalityAsync(consensus.ProcessAsync, attempt.Token);
            }
            catch (Exception error) when (!requestToken.IsCancellationRequested)
            {
                logger.LogWarning("Consensus repair waiting: {Reason}", error.Message);
            }
        }

        async Task PollOptimisticAsync(CancellationToken requestToken)
        {
            try
            {
                using CancellationTokenSource attempt = CancellationTokenSource.CreateLinkedTokenSource(requestToken);
                attempt.CancelAfter(TimeSpan.FromSeconds(15));
                await beacon.PollOptimisticAsync(consensus.ProcessAsync, attempt.Token);
            }
            catch (Exception error) when (!requestToken.IsCancellationRequested)
            {
                logger.LogDebug("Optimistic update waiting: {Reason}", error.Message);
            }
        }
    }

    internal static void ConfigureNetworkBuffers()
    {
        Environment.SetEnvironmentVariable("io.netty.allocator.pageSize", "4096");
        Environment.SetEnvironmentVariable("io.netty.allocator.maxOrder", "9");
        Environment.SetEnvironmentVariable("io.netty.allocator.numHeapArenas", "2");
        Environment.SetEnvironmentVariable("io.netty.allocator.numDirectArenas", "2");
        Environment.SetEnvironmentVariable("io.netty.allocator.tinyCacheSize", "32");
        Environment.SetEnvironmentVariable("io.netty.allocator.smallCacheSize", "16");
        Environment.SetEnvironmentVariable("io.netty.allocator.normalCacheSize", "8");
        Environment.SetEnvironmentVariable("io.netty.allocator.maxCachedBufferCapacity", "16384");
        Environment.SetEnvironmentVariable("io.netty.allocator.cacheTrimInterval", "256");
        InitializeNetworkBuffers();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void InitializeNetworkBuffers()
    {
        NethermindBuffers.Default = PooledByteBufferAllocator.Default;
        NethermindBuffers.RlpxAllocator = PooledByteBufferAllocator.Default;
        NethermindBuffers.DiscoveryAllocator = PooledByteBufferAllocator.Default;
    }
}
