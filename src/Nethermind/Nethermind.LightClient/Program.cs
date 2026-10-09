// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.LightClient;
using Nethermind.LightClient.Consensus;
using Nethermind.Logging.Microsoft;
using Nethermind.Logging.NLog;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NLog.Config;
using NLog.Targets;

if (Array.Exists(args, static arg => arg is "--help" or "-h"))
{
    Console.WriteLine("Nethermind light client\n" +
        "Optional: --checkpoint <trusted beacon block root> or --checkpoint-url <trusted HTTPS Beacon API>\n" +
        "Optional: --network mainnet|hoodi|sepolia (default mainnet) --urls http://127.0.0.1:8545\n" +
        "          --data-dir src/Nethermind/artifacts/lightclient\n" +
        "Mainnet needs --checkpoint or --checkpoint-url; Hoodi and Sepolia use EF-operated defaults.\n" +
        "See README.md for verified RPC selectors and checkpoint trust requirements.");
    return;
}

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
string network = builder.Configuration["network"] ?? "mainnet";
BeaconChainSpec spec = network switch
{
    "mainnet" => BeaconChainSpec.Mainnet,
    "hoodi" => BeaconChainSpec.Hoodi,
    "sepolia" => BeaconChainSpec.Sepolia,
    _ => throw new ArgumentException("Supported networks: mainnet, hoodi, sepolia."),
};
string? checkpointValue = builder.Configuration["checkpoint"];
Hash256 checkpoint;
if (checkpointValue is not null)
{
    checkpoint = new(checkpointValue);
}
else
{
    string checkpointUrl = builder.Configuration["checkpoint-url"] ?? CheckpointSource.DefaultUrl(network)
        ?? throw new ArgumentException("Mainnet requires --checkpoint or --checkpoint-url; no public EF mainnet checkpoint endpoint is available.");
    using HttpClientHandler handler = new() { AllowAutoRedirect = false };
    using HttpClient client = new(handler) { Timeout = TimeSpan.FromSeconds(10) };
    checkpoint = await CheckpointSource.FetchAsync(client, checkpointUrl, CancellationToken.None);
    Console.WriteLine($"Trusted checkpoint from {checkpointUrl}: {checkpoint}");
}
string rpcUrl = builder.Configuration["urls"] ?? "http://127.0.0.1:8545";
bool redirected = Console.IsOutputRedirected;
Console.Write(LightClientBanner.Render(network, rpcUrl, checkpoint.ToString(), redirected));
builder.WebHost.UseUrls(rpcUrl);
builder.WebHost.ConfigureKestrel(static options => options.Limits.MaxRequestBodySize = RpcEndpoint.MaxRequestBodySize);
await using WebApplication app = builder.Build();
ILogger logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("LightClient");
await using BeaconPeerTransport beacon = new(spec, logManager);
await beacon.StartAsync(app.Lifetime.ApplicationStopping);
logger.LogInformation("Beacon P2P started; searching for light-client peers");
TimeProvider clock = TimeProvider.System;
ulong CurrentSlot() => spec.GetSlotAtTime((ulong)clock.GetUtcNow().ToUnixTimeSeconds());
using CancellationTokenSource bootstrapTimeout = CancellationTokenSource.CreateLinkedTokenSource(app.Lifetime.ApplicationStopping);
bootstrapTimeout.CancelAfter(TimeSpan.FromMinutes(2));
string dataDirectory = builder.Configuration["data-dir"] ?? Path.Combine("src", "Nethermind", "artifacts", "lightclient");
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
        value => acceptedStore = new(spec, checkpoint, value, CurrentSlot()), bootstrapTimeout.Token);
    store = acceptedStore ?? throw new InvalidDataException("Beacon peers did not supply a valid trusted checkpoint.");
    await journal.InitializeAsync(bootstrap, store, bootstrapTimeout.Token);
    logger.LogInformation("Trusted checkpoint accepted");
}
LightClientHeader initialHeader = store.FinalizedHeader;
Hash256 initialHash = initialHeader.Execution?.BlockHash ?? initialHeader.ExecutionBlockHash
    ?? throw new InvalidDataException("Verified beacon header has no execution block hash.");
VerifiedHead provisionalHead = new(initialHeader.Beacon!.Slot, initialHeader.Execution?.BlockNumber ?? 0,
    initialHash, initialHeader.Execution?.StateRoot ?? Hash256.Zero);
VerifiedHead? head = null;
VerifiedHead? latestHead = null;
ulong finalizedExecutionTimestamp = 0;
ulong ExecutionTime(VerifiedHead verified) => spec.GenesisTime + verified.Slot * spec.SecondsPerSlot;
await using ExecutionPeerTransport execution = new(network, logManager,
    provisionalHead, ExecutionTime(provisionalHead));
await execution.StartAsync();
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
async Task PublishFinalizedAsync(CancellationToken token)
{
    LightClientHeader header = store.FinalizedHeader;
    Hash256 hash = header.Execution?.BlockHash ?? header.ExecutionBlockHash
        ?? throw new InvalidDataException("Verified finalized header has no execution block hash.");
    VerifiedHead? current = Volatile.Read(ref head);
    if (current?.BlockHash == hash && current.Slot == header.Beacon!.Slot) return;
    (VerifiedHead verified, ulong timestamp) = current?.BlockHash == hash
        ? (current with { Slot = header.Beacon!.Slot }, finalizedExecutionTimestamp)
        : await ResolveAsync(header, token);
    finalizedExecutionTimestamp = timestamp;
    execution.UpdateHead(verified, timestamp);
    Volatile.Write(ref head, verified);
    Volatile.Write(ref latestHead, verified);
    logger.LogInformation("Finalized execution block {BlockNumber} verified", verified.Number);
}
async Task PublishOptimisticAsync(CancellationToken token)
{
    LightClientHeader header = store.OptimisticHeader;
    Hash256 optimisticHash = header.Execution?.BlockHash ?? header.ExecutionBlockHash
        ?? throw new InvalidDataException("Verified optimistic header has no execution block hash.");
    VerifiedHead? current = Volatile.Read(ref latestHead);
    if (current?.BlockHash == optimisticHash && current.Slot == header.Beacon!.Slot) return;
    (VerifiedHead verified, _) = current?.BlockHash == optimisticHash
        ? (current with { Slot = header.Beacon!.Slot }, ExecutionTime(current))
        : await ResolveAsync(header, token);
    if (Volatile.Read(ref head) is { } finalized && verified.Number < finalized.Number) return;
    Volatile.Write(ref latestHead, verified);
}
if (initialHeader.Execution is not null)
    await PublishFinalizedAsync(bootstrapTimeout.Token);
VerifiedHead GetHead()
{
    VerifiedHead snapshot = Volatile.Read(ref head) ?? throw new RpcException(-32000, "Waiting for an authenticated execution header.");
    if (!IsHeadFresh(snapshot, 3600))
        throw new RpcException(-32000, "Verified finality is more than one hour old; waiting for consensus sync.");
    return snapshot;
}
VerifiedHead? GetLatestHead()
{
    VerifiedHead? snapshot = Volatile.Read(ref latestHead);
    return IsHeadFresh(snapshot, 3600) ? snapshot : null;
}
bool IsHeadFresh(VerifiedHead? snapshot, ulong maximumAgeSeconds)
{
    if (snapshot is null) return false;
    ulong slot = CurrentSlot();
    return slot >= snapshot.Slot && slot - snapshot.Slot <= maximumAgeSeconds / spec.SecondsPerSlot;
}
using VerifiedCall calls = new(execution, execution.SpecProvider, logManager);
VerifiedRpc rpc = new(execution, GetHead, spec.ChainId, calls, execution.SpecProvider, GetLatestHead);
app.MapPost("/", (HttpContext context) => RpcEndpoint.HandleAsync(context, rpc, logger));
try { await LightClientHost.RunAsync(app, SyncAsync, logger); }
finally { NLog.LogManager.Shutdown(); }

Task PersistUpdateAsync(LightClientUpdate update, CancellationToken token) =>
    PersistAsync(journal.AppendAsync(update, store, token), token);

async Task PersistAsync(Task write, CancellationToken token)
{
    try { await write; }
    catch (Exception exception) when (!token.IsCancellationRequested)
    {
        logger.LogCritical(exception, "Verified consensus state could not be persisted; stopping the client");
        app.Lifetime.StopApplication();
        throw;
    }
}

async Task SyncAsync(CancellationToken cancellationToken)
{
    using CancellationTokenSource peersStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    Task peerCounts = ReportPeersAsync(peersStop.Token);
    (bool HasWarned, VerifiedHead? Head) finalizedWarning = default;
    (bool HasWarned, VerifiedHead? Head) optimisticWarning = default;
    try
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await PublishFinalizedAsync(cancellationToken); }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Waiting for an execution header authenticated by finalized consensus");
            }
            try
            {
                await ConsensusSync.CatchUpAsync(store, spec, beacon.UpdateAsync, CurrentSlot,
                    PersistUpdateAsync, PublishFinalizedAsync, cancellationToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Consensus sync failed; retaining the last verified finalized head");
            }
            try
            {
                LightClientUpdate? bestUpdate = store.BestUpdate;
                ulong slot = CurrentSlot();
                if (bestUpdate is not null && store.ForceUpdate(slot))
                {
                    await PersistAsync(journal.AppendForcedAsync(bestUpdate, slot, store, cancellationToken), cancellationToken);
                    logger.LogWarning("Advanced sync committee without finality after the update timeout; finalized RPC head remains unchanged");
                }
                await PublishOptimisticAsync(cancellationToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Publishing consensus progress failed; retaining the last verified finalized head");
            }
            try
            {
                LightClientFinalityUpdate? update;
                using (CancellationTokenSource attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    attempt.CancelAfter(TimeSpan.FromSeconds(15));
                    update = await beacon.PollFinalityAsync(value => store.Process(value, CurrentSlot()), attempt.Token);
                }
                if (update is not null)
                {
                    await PersistAsync(journal.AppendAsync(update, store, cancellationToken), cancellationToken);
                    await PublishFinalizedAsync(cancellationToken);
                    await PublishOptimisticAsync(cancellationToken);
                }
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogDebug(exception, "Finality poll failed ({ExceptionType}: {Reason})", exception.GetType().Name, exception.Message);
            }
            try
            {
                LightClientOptimisticUpdate? update;
                using (CancellationTokenSource attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    attempt.CancelAfter(TimeSpan.FromSeconds(15));
                    update = await beacon.PollOptimisticAsync(value => store.Process(value, CurrentSlot()), attempt.Token);
                }
                if (update is not null)
                {
                    await PersistAsync(journal.AppendAsync(update, store, cancellationToken), cancellationToken);
                    await PublishOptimisticAsync(cancellationToken);
                }
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogDebug(exception, "Optimistic poll failed ({ExceptionType}: {Reason})", exception.GetType().Name, exception.Message);
            }
            WarnIfStale(Volatile.Read(ref head), 30 * 60, "Finalized", ref finalizedWarning);
            WarnIfStale(Volatile.Read(ref latestHead), 2 * 60, "Optimistic", ref optimisticWarning);
            await Task.Delay(TimeSpan.FromSeconds(spec.SecondsPerSlot), clock, cancellationToken);
        }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    finally
    {
        await peersStop.CancelAsync();
        await peerCounts;
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
