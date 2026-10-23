// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.LightClient;
using Nethermind.LightClient.Consensus;
using Nethermind.Logging.Microsoft;
using Nethermind.Logging.NLog;
using Nethermind.Network;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NLog.Config;
using NLog.Targets;

if (Array.Exists(args, static arg => arg is "--help" or "-h"))
{
    Console.WriteLine("Nethermind light client\n" +
        "Optional: --checkpoint <trusted beacon block root> or --checkpoint-url <trusted HTTPS Beacon API>\n" +
        "          --checkpoint-check-url <independent trusted HTTPS Beacon API>\n" +
        "Optional: --network mainnet|hoodi|sepolia (default mainnet) --urls http://127.0.0.1:8545\n" +
        "          --data-dir src/Nethermind/artifacts/lightclient\n" +
        "          --beacon-port 9050 --beacon-quic-port 9051 (0 disables QUIC) --execution-port 30307\n" +
        "          --host-accounts <0xaddress,0xaddress,...> (complete account state retained in RAM)\n" +
        "          --host-accounts-bal true|false (incremental BAL storage updates; default false)\n" +
        "Mainnet needs --checkpoint or --checkpoint-url; Hoodi and Sepolia use EF-operated defaults.\n" +
        "See README.md for verified RPC selectors and checkpoint trust requirements.");
    return;
}

using CancellationTokenSource shutdown = new();
using PosixSignalRegistration interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, context =>
{
    context.Cancel = true;
    // A dispatched signal may outlive registration disposal.
    try { shutdown.Cancel(); }
    catch (ObjectDisposedException) { }
});
try { await RunClientAsync(shutdown.Token); }
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
    NLog.LogManager.GetLogger("LightClient").Info("Light client stopped.");
}
finally { NLog.LogManager.Shutdown(); }

async Task RunClientAsync(CancellationToken stoppingToken)
{
    const ulong OptimisticHeadMaxAgeSeconds = 120;
    const ulong FinalizedHeadMaxAgeSeconds = 3600;
    stoppingToken.ThrowIfCancellationRequested();
    WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
    LoggingConfiguration loggingConfiguration = new();
    ColoredConsoleTarget consoleTarget = new("console")
    {
        Layout = "${date:format=dd MMM HH\\:mm\\:ss} | ${message} ${exception:format=message}",
    };
    loggingConfiguration.AddTarget(consoleTarget);
    loggingConfiguration.LoggingRules.Add(new LoggingRule("Microsoft.*", NLog.LogLevel.Warn, NLog.LogLevel.Fatal, consoleTarget) { Final = true });
    loggingConfiguration.LoggingRules.Add(new LoggingRule("System.*", NLog.LogLevel.Warn, NLog.LogLevel.Fatal, consoleTarget) { Final = true });
    loggingConfiguration.AddRule(NLog.LogLevel.Info, NLog.LogLevel.Fatal, consoleTarget, "LightClient");
    loggingConfiguration.AddRule(NLog.LogLevel.Info, NLog.LogLevel.Fatal, consoleTarget, "LightClient.*");
    loggingConfiguration.AddRule(NLog.LogLevel.Warn, NLog.LogLevel.Fatal, consoleTarget, "BeaconChain.P2P.*");
    loggingConfiguration.AddRule(NLog.LogLevel.Warn, NLog.LogLevel.Fatal, consoleTarget, "Network.*");
    loggingConfiguration.AddRule(NLog.LogLevel.Warn, NLog.LogLevel.Fatal, consoleTarget, "Synchronization.Peers.*");
    NLog.LogManager.Configuration = loggingConfiguration;
    using NLogManager logManager = new();
    builder.Logging.ClearProviders();
    builder.Services.Replace(ServiceDescriptor.Singleton<ILoggerFactory>(new NethermindLoggerFactory(logManager)));
    builder.Services.AddCors();
    string network = builder.Configuration["network"] ?? "mainnet";
    Address[] hostedAccounts = HostedAccountState.ParseAccounts(builder.Configuration["host-accounts"]);
    bool hostedAccountsBal = builder.Configuration.GetValue<bool>("host-accounts-bal");
    (int beaconPort, int quicPort, int executionPort) = LightClientHost.ReadPorts(builder.Configuration);
    BeaconChainSpec spec = network switch
    {
        "mainnet" => BeaconChainSpec.Mainnet,
        "hoodi" => BeaconChainSpec.Hoodi,
        "sepolia" => BeaconChainSpec.Sepolia,
        _ => throw new ArgumentException("Supported networks: mainnet, hoodi, sepolia."),
    };
    string? checkpointValue = builder.Configuration["checkpoint"];
    string? checkpointCheckUrl = builder.Configuration["checkpoint-check-url"];
    string dataDirectory = builder.Configuration["data-dir"] ?? Path.Combine("src", "Nethermind", "artifacts", "lightclient");
    TimeProvider clock = TimeProvider.System;
    ulong CurrentSlot() => spec.GetSlotAtTime((ulong)clock.GetUtcNow().ToUnixTimeSeconds());
    bool IsHeadFresh(VerifiedHead? snapshot, ulong maximumAgeSeconds) =>
        snapshot?.IsFresh(CurrentSlot(), maximumAgeSeconds / spec.SecondsPerSlot) ?? false;
    LightClientStatus status = new(spec.ChainId, hostedAccounts,
        latest => IsHeadFresh(latest, OptimisticHeadMaxAgeSeconds), finalized => IsHeadFresh(finalized, FinalizedHeadMaxAgeSeconds));
    string rpcUrl = builder.Configuration["urls"] ?? "http://127.0.0.1:8545";
    builder.WebHost.UseUrls(rpcUrl);
    builder.WebHost.ConfigureKestrel(static options => options.Limits.MaxRequestBodySize = RpcEndpoint.MaxRequestBodySize);
    await using WebApplication app = builder.Build();
    using CancellationTokenRegistration stopApplication = stoppingToken.Register(app.Lifetime.StopApplication);
    using CancellationTokenRegistration stopClient = app.Lifetime.ApplicationStopping.Register(shutdown.Cancel);
    stoppingToken.ThrowIfCancellationRequested();
    ILogger logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("LightClient");
    LightClientHost.ConfigureRequestPolicy(app, rpcUrl);
    app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15), KeepAliveTimeout = TimeSpan.FromSeconds(15) });
    app.MapPost("/", (HttpContext context) => RpcEndpoint.HandleAsync(context, status.InvokeAsync, logger,
        stoppingToken: app.Lifetime.ApplicationStopping));
    app.MapGet("/status", status.Read);
    app.MapGet("/ws", status.HandleWebSocketAsync);
    await LightClientHost.RunAsync(app, async token =>
    {
        try { await InitializeAsync(token); }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            status.SetStage(StartupStage.Failed);
            throw;
        }
    }, logger);

    async Task InitializeAsync(CancellationToken startupToken)
    {
        Hash256 checkpoint;
        if (checkpointValue is not null)
        {
            checkpoint = new(checkpointValue);
            if (checkpointCheckUrl is not null)
            {
                using HttpClientHandler handler = new() { AllowAutoRedirect = false };
                using HttpClient client = new(handler) { Timeout = TimeSpan.FromSeconds(10) };
                await CheckpointSource.CrossCheckAsync(client, checkpointCheckUrl, checkpoint, spec, startupToken);
            }
        }
        else
        {
            string checkpointUrl = builder.Configuration["checkpoint-url"] ?? CheckpointSource.DefaultUrl(network)
                ?? throw new ArgumentException("Mainnet requires --checkpoint or --checkpoint-url; no public EF mainnet checkpoint endpoint is available.");
            using HttpClientHandler handler = new() { AllowAutoRedirect = false };
            using HttpClient client = new(handler) { Timeout = TimeSpan.FromSeconds(10) };
            CheckpointSelection selection = await CheckpointSource.ResolveAsync(client, checkpointUrl, network, dataDirectory,
                spec, CurrentSlot(), checkpointCheckUrl, startupToken);
            checkpoint = selection.Root;
            Console.WriteLine($"{(selection.Reused ? "Reusing" : "Selected")} trusted checkpoint from {selection.Provider} (epoch {selection.Epoch}): {checkpoint}");
        }
        bool redirected = Console.IsOutputRedirected;
        Console.Write(LightClientBanner.Render(network, rpcUrl, checkpoint.ToString(), redirected, beaconPort, executionPort));
        status.SetStage(StartupStage.Beacon);
        using SemaphoreSlim consensusGate = new(1, 1);
        using ColumnsDb<BeaconChainDbColumns> beaconDb = BeaconPeerTransport.OpenPeerDb(dataDirectory, spec, logManager);
        using IFullDb beaconPeersDb = BoundedEnrStorage.OpenDb(Path.Combine(dataDirectory, "cl", spec.ChainId.ToString(), "discovery"), logManager, beacon: true);
        await using BeaconPeerTransport beacon = new(spec, logManager, beaconDb, new BoundedEnrStorage(new NetworkStorage(beaconPeersDb, logManager)),
            p2pPort: beaconPort, discoveryPort: beaconPort, time: clock, quicPort: quicPort);
        await beacon.StartAsync(app.Lifetime.ApplicationStopping);
        logger.LogInformation("Beacon P2P started; searching for light-client peers");
        status.SetStage(StartupStage.Bootstrap);
        using CancellationTokenSource bootstrapTimeout = CancellationTokenSource.CreateLinkedTokenSource(app.Lifetime.ApplicationStopping);
        bootstrapTimeout.CancelAfter(TimeSpan.FromMinutes(2));
        VerifiedConsensusJournal journal = new(dataDirectory, spec, network, checkpoint, logger);
        LightClientStore? resumed = await journal.LoadAsync(CurrentSlot(), bootstrapTimeout.Token);
        LightClientStore store;
        if (resumed is not null)
        {
            store = resumed;
            logger.LogInformation("Replayed verified light-client state from {Directory}", dataDirectory);
        }
        else
        {
            LightClientStore? acceptedStore = null;
            LightClientBootstrap bootstrap = await beacon.BootstrapAsync(checkpoint,
                (value, _) =>
                {
                    acceptedStore = new(spec, checkpoint, value, CurrentSlot());
                    return Task.CompletedTask;
                }, bootstrapTimeout.Token);
            store = acceptedStore ?? throw new InvalidDataException("Beacon peers did not supply a valid trusted checkpoint.");
            await journal.InitializeAsync(bootstrap, store, bootstrapTimeout.Token);
            logger.LogInformation("Trusted checkpoint accepted");
        }
        LightClientHeader initialHeader = store.FinalizedHeader;
        LightClientHeader persistedFinalized = store.FinalizedHeader;
        LightClientHeader persistedOptimistic = store.OptimisticHeader;
        Hash256 initialHash = initialHeader.Execution?.BlockHash ?? initialHeader.ExecutionBlockHash
            ?? throw new InvalidDataException("Verified beacon header has no execution block hash.");
        VerifiedHead provisionalHead = new(initialHeader.Beacon!.Slot, initialHeader.Execution?.BlockNumber ?? 0,
            initialHash, initialHeader.Execution?.StateRoot ?? Hash256.Zero);
        ulong ExecutionTime(VerifiedHead verified) => spec.GenesisTime + verified.Slot * spec.SecondsPerSlot;
        status.SetStage(StartupStage.Execution);
        await using ExecutionPeerTransport execution = new(network, logManager,
            provisionalHead, ExecutionTime(provisionalHead), dataDirectory: dataDirectory, hostedAccounts: hostedAccounts,
            hostedAccountsBal: hostedAccountsBal, port: executionPort);
        await execution.StartAsync(app.Lifetime.ApplicationStopping);
        logger.LogInformation("Execution P2P started; searching for SNAP peers via {Domain}", execution.DiscoveryDomain);
        async Task<(VerifiedHead Head, ulong Timestamp)> ResolveAsync(LightClientHeader header, CancellationToken token)
        {
            if (header.Execution is { } payload)
            {
                VerifiedHead verified = new(header.Beacon!.Slot, payload.BlockNumber, payload.BlockHash!, payload.StateRoot!);
                return (verified, ExecutionTime(verified));
            }
            Hash256 hash = header.ExecutionBlockHash ?? throw new InvalidDataException("Verified Gloas header has no execution block hash.");
            BlockHeader executionHeader = await execution.GetHeaderByHashAsync(hash, token);
            return (new(header.Beacon!.Slot, executionHeader.Number, hash, executionHeader.StateRoot!), executionHeader.Timestamp);
        }
        ConsensusPublisher publisher = new(() => Volatile.Read(ref persistedFinalized),
            () => Volatile.Read(ref persistedOptimistic), ResolveAsync, execution.UpdateHead, clock);
        if (initialHeader.Execution is not null)
        {
            await publisher.PublishFinalizedAsync(bootstrapTimeout.Token);
            logger.LogInformation("Finalized execution block {BlockNumber} verified", publisher.Heads.Finalized!.Number);
        }
        VerifiedHead GetHead() =>
            publisher.Heads.Finalized ?? throw new RpcException(-32000, "Waiting for an authenticated execution header.");
        VerifiedHead? GetLatestHead() => publisher.Heads.Latest;
        VerifiedHead? GetFreshLatestHead()
        {
            VerifiedHead? snapshot = GetLatestHead();
            return IsHeadFresh(snapshot, OptimisticHeadMaxAgeSeconds) ? snapshot : null;
        }
        using VerifiedCall calls = new(execution, execution.SpecProvider, logManager);
        using VerifiedRpc rpc = new(execution, GetHead, spec.ChainId, calls, execution.SpecProvider, GetLatestHead,
            latest => IsHeadFresh(latest, OptimisticHeadMaxAgeSeconds), finalized => IsHeadFresh(finalized, FinalizedHeadMaxAgeSeconds));
        VerifiedSubscriptions subscriptions = new(execution, rpc, logger);
        try
        {
            beacon.StartGossip(ProcessFinalityAsync, ProcessOptimisticAsync, app.Lifetime.ApplicationStopping);
            status.Initialize(rpc.InvokeAsync, subscriptions.HandleAsync, GetLatestHead, () => publisher.Heads.Finalized,
                execution.GetHostedAccountProgress, () => execution.LogHistoryProgress);
            await SyncAsync(app.Lifetime.ApplicationStopping);
        }
        finally
        {
            status.SetStage(StartupStage.Stopping);
            app.Lifetime.StopApplication();
            try { await subscriptions.DisposeAsync(); }
            finally { await app.StopAsync(CancellationToken.None); }
        }

        Task ProcessUpdateAsync(LightClientUpdate update, CancellationToken token) =>
            ProcessConsensusAsync(() => store.Process(update, CurrentSlot()), () => store.FinalizedHeader.Beacon!.Slot,
                stopping => journal.AppendAsync(update, store, stopping), token);

        Task<bool> ProcessFinalityAsync(LightClientFinalityUpdate update, CancellationToken token) =>
            ProcessConsensusAsync(() => store.Process(update, CurrentSlot()), () => store.FinalizedHeader.Beacon!.Slot,
                stopping => journal.AppendAsync(update, store, stopping), token);

        Task<bool> ProcessOptimisticAsync(LightClientOptimisticUpdate update, CancellationToken token) =>
            ProcessConsensusAsync(() => store.Process(update, CurrentSlot()), () => store.OptimisticHeader.Beacon!.Slot,
                stopping => journal.AppendAsync(update, store, stopping), token);

        async Task<bool> ProcessConsensusAsync(Action process, Func<ulong> slot, Func<CancellationToken, Task> persist, CancellationToken token)
        {
            await consensusGate.WaitAsync(token);
            try
            {
                token.ThrowIfCancellationRequested();
                ulong before = slot();
                process();
                // Once authenticated, an update is durably recorded before another mutation, independently of a peer's request deadline.
                await PersistAsync(persist(app.Lifetime.ApplicationStopping), app.Lifetime.ApplicationStopping);
                PublishPersistedHeaders();
                return slot() > before;
            }
            finally { consensusGate.Release(); }
        }

        void PublishPersistedHeaders()
        {
            Volatile.Write(ref persistedFinalized, store.FinalizedHeader);
            Volatile.Write(ref persistedOptimistic, store.OptimisticHeader);
        }

        async Task PersistAsync(Task write, CancellationToken token)
        {
            try { await write; }
            catch (Exception exception) when (!token.IsCancellationRequested)
            {
                logger.LogCritical(exception, "Verified consensus state could not be persisted; stopping the client");
                app.Lifetime.StopApplication();
                throw new IOException("Verified consensus persistence failed.", exception);
            }
        }

        async Task SyncAsync(CancellationToken cancellationToken)
        {
            using CancellationTokenSource workersStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task[] workers = [PollConsensusAsync(workersStop.Token), PublishConsensusAsync(workersStop.Token),
                ReportPeersAsync(workersStop.Token), beacon.GossipCompletion,
                execution.HostAccountsAsync(GetFreshLatestHead, clock, workersStop.Token)];
            await Task.WhenAny(workers);
            app.Lifetime.StopApplication();
            await workersStop.CancelAsync();
            await Task.WhenAll(workers);
        }

        Task PublishConsensusAsync(CancellationToken cancellationToken)
        {
            (bool HasWarned, VerifiedHead? Head) finalizedWarning = default;
            (bool HasWarned, VerifiedHead? Head) optimisticWarning = default;
            Hash256? subscriptionHash = null;
            VerifiedHead? loggedFinalized = publisher.Heads.Finalized;
            return publisher.RunAsync(Observe, ReportFailure, cancellationToken);

            void ReportFailure(bool finalized, Exception exception)
            {
                if (finalized)
                    logger.LogWarning(exception, "Waiting for an execution header authenticated by finalized consensus");
                else
                    logger.LogWarning(exception, "Publishing consensus progress failed; retaining the last verified finalized head");
            }

            void Observe(ConsensusPublisher.PublishedHeads snapshot)
            {
                if (snapshot.Finalized is { } finalized && finalized != loggedFinalized)
                {
                    logger.LogInformation("Finalized execution block {BlockNumber} verified", finalized.Number);
                    loggedFinalized = finalized;
                }
                WarnIfStale(snapshot.Finalized, 30 * 60, "Finalized", ref finalizedWarning);
                WarnIfStale(snapshot.Latest, OptimisticHeadMaxAgeSeconds, "Optimistic", ref optimisticWarning);
                if (snapshot.Latest is { } observed && IsHeadFresh(observed, OptimisticHeadMaxAgeSeconds) &&
                    observed.BlockHash != subscriptionHash)
                {
                    subscriptions.PublishHead(observed);
                    subscriptionHash = observed.BlockHash;
                }
            }

            void WarnIfStale(VerifiedHead? current, ulong maximumAgeSeconds, string kind,
                ref (bool HasWarned, VerifiedHead? Head) warning)
            {
                if (IsHeadFresh(current, maximumAgeSeconds))
                {
                    warning = default;
                    return;
                }
                if (warning.HasWarned && warning.Head == current) return;

                logger.LogWarning("{HeadKind} light-client head is stale or unavailable (beacon slot {Slot}, execution block {BlockNumber}); waiting for authenticated P2P updates",
                    kind, current?.Slot, current?.Number);
                warning = (true, current);
            }
        }

        async Task PollConsensusAsync(CancellationToken cancellationToken)
        {
            try
            {
                await ConsensusSync.RunRepairLoopsAsync(RepairConsensusAsync, PollOptimisticAsync,
                    () => Volatile.Read(ref persistedOptimistic).Beacon!.Slot, CurrentSlot,
                    TimeSpan.FromSeconds(spec.SecondsPerSlot), clock, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        }

        async Task RepairConsensusAsync(CancellationToken cancellationToken)
        {
            try
            {
                await ConsensusSync.CatchUpAsync(store, spec, beacon.UpdateAsync, CurrentSlot,
                    ProcessUpdateAsync, cancellationToken);
                await consensusGate.WaitAsync(cancellationToken);
                try
                {
                    LightClientUpdate? bestUpdate = store.BestUpdate;
                    ulong slot = CurrentSlot();
                    if (bestUpdate is not null && store.ForceUpdate(slot))
                    {
                        await PersistAsync(journal.AppendForcedAsync(bestUpdate, slot, store, app.Lifetime.ApplicationStopping),
                            app.Lifetime.ApplicationStopping);
                        PublishPersistedHeaders();
                        logger.LogWarning("Advanced sync committee without finality after the update timeout; finalized RPC head remains unchanged");
                    }
                }
                finally { consensusGate.Release(); }
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Consensus sync failed; retaining the last verified finalized head");
            }
            try
            {
                using CancellationTokenSource attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attempt.CancelAfter(TimeSpan.FromSeconds(15));
                await beacon.PollFinalityAsync(async (update, token) =>
                {
                    await ProcessFinalityAsync(update, token);
                    return true;
                }, attempt.Token);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogDebug(exception, "Finality poll failed ({ExceptionType}: {Reason})", exception.GetType().Name, exception.Message);
            }
        }

        async Task PollOptimisticAsync(CancellationToken cancellationToken)
        {
            try
            {
                using CancellationTokenSource attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attempt.CancelAfter(TimeSpan.FromSeconds(15));
                await beacon.PollOptimisticAsync(ProcessOptimisticAsync, attempt.Token);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogDebug(exception, "Optimistic poll failed ({ExceptionType}: {Reason})", exception.GetType().Name, exception.Message);
            }
        }

        async Task ReportPeersAsync(CancellationToken cancellationToken)
        {
            try
            {
                using PeriodicTimer timer = new(TimeSpan.FromSeconds(12));
                do
                {
                    logger.LogInformation("Peers: beacon {BeaconPeers}, execution {ExecutionPeers}, SNAP {SnapPeers} ({ExecutionCandidates} candidates, DNS {DnsStatus})",
                        beacon.PeerCount, execution.PeerCount, execution.SnapPeerCount, execution.CandidateCount, execution.DiscoveryStatus);
                } while (await timer.WaitForNextTickAsync(cancellationToken));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        }
    }
}
