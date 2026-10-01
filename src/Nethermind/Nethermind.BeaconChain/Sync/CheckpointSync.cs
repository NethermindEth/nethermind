// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Types;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.BeaconChain.Sync;

/// <summary>The verified finalized checkpoint the driver bootstraps from.</summary>
public record CheckpointAnchor(ForkedBeaconState State, ForkedSignedBeaconBlock Block, Hash256 BlockRoot, Hash256 StateRoot);

/// <summary>Bootstraps the beacon chain from a finalized checkpoint state and block.</summary>
/// <remarks>
/// Downloads the finalized state from the configured beacon API (or reads it from
/// <see cref="IBeaconChainConfig.CheckpointStateFile"/>), recomputes its hash tree root, derives
/// the anchor block root from <c>state.LatestBlockHeader</c>, fetches and cross-verifies the
/// anchor block, and persists everything to the <see cref="BeaconChainStore"/>. Only Fulu and Gloas
/// states are supported, each decoded in the layout of the fork its slot belongs to.
/// </remarks>
public class CheckpointSync(
    IBeaconChainConfig config,
    BeaconChainSpec spec,
    BeaconChainStore store,
    ILogManager logManager) : IDisposable
{
    private const string OctetStreamMediaType = "application/octet-stream";
    private const string ConsensusVersionHeader = "Eth-Consensus-Version";
    /// <summary>Fallback initial buffer size when the state response has no Content-Length.</summary>
    private const int DefaultStateBufferSize = 64 * 1024 * 1024;
    /// <summary>Fallback initial buffer size when the anchor block response has no Content-Length.</summary>
    private const int DefaultBlockBufferSize = 1024 * 1024;
    private const int DefaultMaxDownloadAttempts = 5;
    private static readonly TimeSpan DefaultRetryBaseDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultReadStallTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DefaultResponseHeadersTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Largest checkpoint body read, 1 GiB: about four times a mainnet state of hundreds of MB; an operational bound, not an SSZ limit.</summary>
    internal int MaxBodyBytes { get; init; } = 1024 * 1024 * 1024;

    /// <summary>Lowest average rate, in bytes per second, a response body may arrive at once <see cref="MinThroughputGrace"/> has passed: 64 KiB/s, the slowest link the download supports.</summary>
    /// <remarks>
    /// A provider that sends a byte just inside every <see cref="ReadStallTimeout"/> would otherwise hold startup without limit. At this rate a
    /// 300 MB state takes about 80 minutes, and an attempt lasts at most <see cref="MaxBodyBytes"/> / rate plus the grace, about 4.6 hours.
    /// </remarks>
    internal int MinThroughputBytesPerSecond { get; init; } = 64 * 1024;

    /// <summary>Time a response body may arrive below <see cref="MinThroughputBytesPerSecond"/> before the rate is enforced, for connection ramp-up.</summary>
    internal TimeSpan MinThroughputGrace { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Total time allowed for a checkpoint response body, default five hours.</summary>
    internal TimeSpan BodyDownloadTimeout { get; init; } = TimeSpan.FromHours(5);

    private readonly ILogger _logger = logManager.GetClassLogger<CheckpointSync>();
    private readonly HttpClient _httpClient = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
    {
        Timeout = DefaultResponseHeadersTimeout,
    };

    /// <summary>Attempts a checkpoint download gets before its last failure is thrown.</summary>
    internal int MaxDownloadAttempts { get; init; } = DefaultMaxDownloadAttempts;

    /// <summary>Delay after the first failed attempt; it doubles per attempt up to <see cref="MaxRetryDelay"/>.</summary>
    internal TimeSpan RetryBaseDelay { get; init; } = DefaultRetryBaseDelay;

    /// <summary>Pool the state bytes are read into; every array rented from it is returned, including those of a dropped attempt.</summary>
    internal ArrayPool<byte> BufferPool { get; init; } = ArrayPool<byte>.Shared;

    /// <summary>Longest wait for the next bytes of a response body before the download counts as dropped and is retried.</summary>
    /// <remarks>
    /// <see cref="HttpClient.Timeout"/> stops at the response headers under <see cref="HttpCompletionOption.ResponseHeadersRead"/>,
    /// so a provider that vanishes mid-body without its FIN or RST reaching this host would otherwise leave the read waiting forever.
    /// </remarks>
    internal TimeSpan ReadStallTimeout { get; init; } = DefaultReadStallTimeout;

    /// <summary>Longest wait for a request to connect and receive its response headers before the attempt counts as dropped and is retried.</summary>
    /// <remarks>
    /// It is <see cref="HttpClient.Timeout"/>, which under <see cref="HttpCompletionOption.ResponseHeadersRead"/> covers the connect and the
    /// headers only; the body, however large, is bounded per read by <see cref="ReadStallTimeout"/> instead.
    /// </remarks>
    internal TimeSpan ResponseHeadersTimeout
    {
        get => _httpClient.Timeout;
        init => _httpClient.Timeout = value;
    }

    /// <summary>
    /// The operator-configured URL if set, otherwise the selected network's default provider.
    /// An operator override always wins, matching the bootnodes override convention.
    /// </summary>
    public string EffectiveCheckpointSyncUrl => !string.IsNullOrWhiteSpace(config.CheckpointSyncUrl)
        ? config.CheckpointSyncUrl
        : spec.CheckpointSyncUrl
            ?? throw new InvalidOperationException($"No checkpoint sync URL is configured and chain id {spec.ChainId} has no default; set Beacon.CheckpointSyncUrl explicitly.");

    public async Task<CheckpointAnchor> RunAsync(CancellationToken cancellationToken)
    {
        Checkpoint? weakSubjectivityCheckpoint = ParseWeakSubjectivityCheckpoint(config.WeakSubjectivityCheckpoint);
        (byte[] buffer, int length) = config.CheckpointStateFile is { } stateFile
            ? await ReadStateFileAsync(stateFile, cancellationToken)
            : await RetryTransientAsync("state download", DownloadStateAsync, cancellationToken);

        try
        {
            ForkedBeaconState state = DecodeState(buffer.AsSpan(0, length));
            ThrowIfWrongNetwork(state, spec);
            ThrowIfInvalidSyncCommitteeKeys(state);

            Stopwatch stopwatch = Stopwatch.StartNew();
            Hash256 stateRoot = HashTreeRoot(state);
            if (_logger.IsInfo) _logger.Info($"Computed {state.Fork} checkpoint state root {stateRoot} ({ValidatorCount(state)} validators) in {stopwatch.Elapsed.TotalSeconds:F1} s");

            BeaconBlockHeader latestBlockHeader = LatestBlockHeader(state);
            Hash256 blockRoot = ComputeAnchorBlockRoot(latestBlockHeader, stateRoot);
            if (weakSubjectivityCheckpoint is not null && !ProvesCheckpoint(state, blockRoot, weakSubjectivityCheckpoint))
            {
                throw new InvalidDataException($"The anchor at slot {state.Slot} is not BeaconChain.WeakSubjectivityCheckpoint {config.WeakSubjectivityCheckpoint}: the anchor block must be the checkpoint's block at the start of epoch {weakSubjectivityCheckpoint.Epoch}. Supply the checkpoint's state with BeaconChain.CheckpointStateFile or a source serving it as finalized, or correct the checkpoint.");
            }

            Hash256 blockStateRoot = latestBlockHeader.StateRoot == Hash256.Zero ? stateRoot : latestBlockHeader.StateRoot!;
            ForkedSignedBeaconBlock block = await GetAnchorBlockAsync(state, blockRoot, blockStateRoot, cancellationToken);

            if (state.Slot > latestBlockHeader.Slot)
            {
                // specs/gloas/fork-choice.md get_forkchoice_store asserts anchor_block.state_root == hash_tree_root(anchor_state).
                (byte[] postState, int postLength) = await RetryTransientAsync("anchor post-state download", async token =>
                {
                    using HttpResponseMessage response = await GetOctetStreamAsync($"/eth/v2/debug/beacon/states/{blockStateRoot}", token);
                    return await ReadResponseBodyAsync(response, DefaultStateBufferSize, token);
                }, cancellationToken);
                BufferPool.Return(buffer);
                buffer = postState;
                length = postLength;
                state = DecodeState(buffer.AsSpan(0, length));
                ThrowIfWrongNetwork(state, spec);
                ThrowIfInvalidSyncCommitteeKeys(state);
                stateRoot = HashTreeRoot(state);
                if (state.Slot != block.Slot || stateRoot != blockStateRoot
                    || ComputeAnchorBlockRoot(LatestBlockHeader(state), stateRoot) != blockRoot)
                {
                    throw new InvalidDataException($"Anchor post-state does not match block {blockRoot} at slot {block.Slot}.");
                }
            }

            CheckpointAnchor anchor = new(state, block, blockRoot, stateRoot);
            Persist(anchor, buffer.AsSpan(0, length), weakSubjectivityCheckpoint);
            if (_logger.IsInfo) _logger.Info($"Checkpoint sync complete: anchor block {blockRoot} at slot {latestBlockHeader.Slot}");
            return anchor;
        }
        finally
        {
            BufferPool.Return(buffer);
        }
    }

    private async Task<(byte[] Buffer, int Length)> DownloadStateAsync(CancellationToken cancellationToken)
    {
        if (_logger.IsInfo) _logger.Info($"Downloading finalized beacon state from {EffectiveCheckpointSyncUrl}");
        Stopwatch stopwatch = Stopwatch.StartNew();

        using HttpResponseMessage response = await GetOctetStreamAsync("/eth/v2/debug/beacon/states/finalized", cancellationToken);
        ThrowIfUnsupportedFork(response.Headers.TryGetValues(ConsensusVersionHeader, out IEnumerable<string>? values) ? values.FirstOrDefault() : null);

        (byte[] buffer, int length) = await ReadResponseBodyAsync(response, DefaultStateBufferSize, cancellationToken);

        if (_logger.IsInfo) _logger.Info($"Downloaded finalized beacon state: {length / (1024.0 * 1024.0):F1} MB in {stopwatch.Elapsed.TotalSeconds:F1} s");
        return (buffer, length);
    }

    private async Task<(byte[] Buffer, int Length)> ReadStateFileAsync(string stateFile, CancellationToken cancellationToken)
    {
        await using FileStream content = File.OpenRead(stateFile);
        ThrowIfBodyTooLarge(content.Length);
        return await ReadToPooledBufferAsync(content, (int)content.Length, Timeout.InfiniteTimeSpan, cancellationToken);
    }

    internal async Task<(byte[] Buffer, int Length)> ReadResponseBodyAsync(HttpResponseMessage response, int defaultLength, CancellationToken cancellationToken)
    {
        long initialLength = response.Content.Headers.ContentLength ?? Math.Min(defaultLength, MaxBodyBytes);
        ThrowIfBodyTooLarge(initialLength);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(BodyDownloadTimeout);
        try
        {
            await using Stream content = await response.Content.ReadAsStreamAsync(deadline.Token);
            return await ReadToPooledBufferAsync(content, (int)initialLength, ReadStallTimeout, deadline.Token, MinThroughputBytesPerSecond);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new IOException($"Checkpoint body exceeded the {BodyDownloadTimeout.TotalSeconds:F0} s download deadline.");
        }
    }

    private void ThrowIfBodyTooLarge(long length)
    {
        if (length > MaxBodyBytes)
        {
            throw new InvalidDataException($"Checkpoint body exceeds the {MaxBodyBytes}-byte limit.");
        }
    }

    /// <param name="minBytesPerSecond">Average rate the body must keep once <see cref="MinThroughputGrace"/> has passed; 0 for none.</param>
    private async Task<(byte[] Buffer, int Length)> ReadToPooledBufferAsync(Stream content, int initialLength, TimeSpan stallTimeout, CancellationToken cancellationToken, int minBytesPerSecond = 0)
    {
        using CancellationTokenSource stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        long started = Stopwatch.GetTimestamp();
        byte[] buffer = BufferPool.Rent(initialLength);
        int length = 0;
        try
        {
            while (true)
            {
                if (length == MaxBodyBytes)
                {
                    byte[] probe = new byte[1];
                    stall.CancelAfter(stallTimeout);
                    ThrowIfBodyTooLarge((long)length + await content.ReadAsync(probe, stall.Token));
                    return (buffer, length);
                }
                if (length == buffer.Length)
                {
                    byte[] grown = BufferPool.Rent((int)Math.Min((long)buffer.Length * 2, MaxBodyBytes));
                    buffer.CopyTo(grown, 0);
                    BufferPool.Return(buffer);
                    buffer = grown;
                }

                stall.CancelAfter(stallTimeout);
                int read = await content.ReadAsync(buffer.AsMemory(length, Math.Min(buffer.Length, MaxBodyBytes) - length), stall.Token);
                if (read == 0)
                {
                    return (buffer, length);
                }

                length += read;
                if (minBytesPerSecond > 0 && Stopwatch.GetElapsedTime(started) > MinThroughputGrace + TimeSpan.FromSeconds((double)length / minBytesPerSecond))
                {
                    throw new IOException($"The body arrived below {minBytesPerSecond / 1024.0:0.##} KiB/s: {length} bytes in {Stopwatch.GetElapsedTime(started).TotalSeconds:F0} s");
                }
            }
        }
        catch (Exception e)
        {
            // A dropped transfer is retried, so its buffer must not be left to the GC once per attempt.
            BufferPool.Return(buffer);
            if (e is OperationCanceledException && stall.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new IOException($"No data arrived for {stallTimeout.TotalSeconds:F0} s");
            }

            throw;
        }
    }

    /// <summary>Whether <paramref name="exception"/> is a network failure a later attempt can outlast: a dropped or timed-out transfer, or a server-side or rate-limit response.</summary>
    /// <remarks>An unsupported fork, undecodable data and the caller's own cancellation are never transient.</remarks>
    internal static bool IsTransientDownloadFailure(Exception exception, CancellationToken cancellationToken) => exception switch
    {
        HttpRequestException { StatusCode: { } status } => status is HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests,
        HttpRequestException or IOException => true,
        OperationCanceledException => !cancellationToken.IsCancellationRequested,
        _ => false,
    };

    /// <summary>The delay after <paramref name="delay"/>: twice as long, never beyond <see cref="MaxRetryDelay"/>.</summary>
    internal static TimeSpan NextRetryDelay(TimeSpan delay) => TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxRetryDelay.Ticks));

    /// <summary>The message of the innermost exception, which names the network failure a wrapping <see cref="HttpRequestException"/> only hints at.</summary>
    internal static string DescribeCause(Exception exception)
    {
        while (exception.InnerException is { } inner)
        {
            exception = inner;
        }

        return exception.Message.TrimEnd('.');
    }

    private async Task<T> RetryTransientAsync<T>(string what, Func<CancellationToken, Task<T>> attempt, CancellationToken cancellationToken)
    {
        TimeSpan delay = RetryBaseDelay;
        for (int attemptNumber = 1; ; attemptNumber++)
        {
            try
            {
                return await attempt(cancellationToken);
            }
            catch (Exception e) when (attemptNumber < MaxDownloadAttempts && IsTransientDownloadFailure(e, cancellationToken))
            {
                if (_logger.IsInfo) _logger.Info($"Checkpoint {what} failed on attempt {attemptNumber} of {MaxDownloadAttempts}: {DescribeCause(e)}; retrying in {delay.TotalSeconds:F0} s.");
                await Task.Delay(delay, cancellationToken);
                delay = NextRetryDelay(delay);
            }
        }
    }

    private ForkedBeaconState DecodeState(ReadOnlySpan<byte> sszBytes)
    {
        ForkedBeaconState state = BeaconStateCodec.DecodeForked(sszBytes, spec);
        ThrowIfUnsupportedFork(state, spec);
        return state;
    }

    /// <summary>
    /// Maps the state's fork version onto the spec schedule, refuses anything before Fulu, and refuses a
    /// version whose fork is not the one the state's slot selected its layout by.
    /// </summary>
    internal static void ThrowIfUnsupportedFork(ForkedBeaconState state, BeaconChainSpec spec)
    {
        byte[] currentVersion = state switch
        {
            ForkedBeaconState.OfFulu fulu => fulu.State.Fork!.CurrentVersion!,
            ForkedBeaconState.OfGloas gloas => gloas.State.Fork!.CurrentVersion!,
            _ => throw new NotSupportedException($"Unhandled beacon state shape {state.GetType().Name}"),
        };
        foreach (ForkScheduleEntry entry in spec.Forks)
        {
            if (entry.Version.AsSpan().SequenceEqual(currentVersion))
            {
                if (entry.Epoch < spec.ElectraForkEpoch)
                {
                    throw new NotSupportedException($"Checkpoint state fork version {currentVersion.ToHexString(true)} predates Electra; the embedded beacon chain driver requires a Fulu or Gloas checkpoint.");
                }

                if (entry.Epoch < spec.FuluForkEpoch)
                {
                    throw new NotSupportedException("Electra checkpoint upgrade not implemented yet");
                }

                // By version identity, not activation epoch: Fulu and Gloas may activate in the same epoch.
                BeaconFork versionFork = currentVersion.AsSpan().SequenceEqual(spec.GloasForkVersion) ? BeaconFork.Gloas : BeaconFork.Fulu;
                if (versionFork != state.Fork)
                {
                    throw new InvalidDataException($"Checkpoint state at slot {state.Slot} carries the {versionFork} fork version {currentVersion.ToHexString(true)}, but its slot belongs to the {state.Fork} fork. Fix the fork configuration or delete the beaconChain database to checkpoint-sync again.");
                }

                return;
            }
        }

        throw new NotSupportedException($"Checkpoint state has unknown fork version {currentVersion.ToHexString(true)}. Fix the fork configuration or delete the beaconChain database to checkpoint-sync again.");
    }

    /// <summary>Refuses an anchor state whose current or next sync committee holds a pubkey that fails BLS <c>KeyValidate</c>, or an <c>aggregate_pubkey</c> that is not the aggregate of its pubkeys.</summary>
    /// <remarks>
    /// Altair <c>eth_aggregate_pubkeys</c> asserts <c>KeyValidate</c> on every member when a committee is built, and
    /// sync-aggregate verification only decodes the keys, so a committee this node did not compute must be checked here.
    /// </remarks>
    /// <exception cref="InvalidDataException">A sync committee pubkey is malformed, the point at infinity, or outside G1, or the committee's <c>aggregate_pubkey</c> does not match.</exception>
    internal static void ThrowIfInvalidSyncCommitteeKeys(ForkedBeaconState state)
    {
        (SyncCommittee current, SyncCommittee next) = state switch
        {
            ForkedBeaconState.OfFulu fulu => (fulu.State.CurrentSyncCommittee!, fulu.State.NextSyncCommittee!),
            ForkedBeaconState.OfGloas gloas => (gloas.State.CurrentSyncCommittee!, gloas.State.NextSyncCommittee!),
            _ => throw new NotSupportedException($"Unhandled beacon state shape {state.GetType().Name}"),
        };
        ThrowIfInvalidSyncCommitteeKeys(current, "current_sync_committee", state.Slot);
        ThrowIfInvalidSyncCommitteeKeys(next, "next_sync_committee", state.Slot);
    }

    /// <summary>Refuses an anchor state that belongs to another network than <paramref name="spec"/>.</summary>
    /// <exception cref="InvalidDataException">The state's <c>genesis_validators_root</c> differs from the network's.</exception>
    internal static void ThrowIfWrongNetwork(ForkedBeaconState state, BeaconChainSpec spec)
    {
        Hash256 genesisValidatorsRoot = state switch
        {
            ForkedBeaconState.OfFulu fulu => fulu.State.GenesisValidatorsRoot!,
            ForkedBeaconState.OfGloas gloas => gloas.State.GenesisValidatorsRoot!,
            _ => throw new NotSupportedException($"Unhandled beacon state shape {state.GetType().Name}"),
        };
        if (genesisValidatorsRoot != spec.GenesisValidatorsRoot)
        {
            throw new InvalidDataException($"The anchor state at slot {state.Slot} has genesis_validators_root {genesisValidatorsRoot}, but this network's is {spec.GenesisValidatorsRoot}; it belongs to another network. Use a checkpoint source of this network, or delete a beaconChain database written for another one.");
        }
    }

    private static void ThrowIfInvalidSyncCommitteeKeys(SyncCommittee committee, string field, ulong slot)
    {
        Bls.P1Affine publicKey = new(stackalloc long[Bls.P1Affine.Sz]);
        BlsSigner.AggregatedPublicKey aggregate = new(stackalloc long[Bls.P1.Sz]);
        BlsPublicKey[] pubkeys = committee.Pubkeys!;
        for (int i = 0; i < pubkeys.Length; i++)
        {
            if (!BlsSignatureSet.TryKeyValidate(pubkeys[i].Bytes, publicKey))
            {
                throw new InvalidDataException($"Anchor state at slot {slot} is refused: {field} pubkey {i} fails BLS KeyValidate (malformed, infinity or outside G1).");
            }

            aggregate.Aggregate(publicKey);
        }

        // Altair get_next_sync_committee: aggregate_pubkey = eth_aggregate_pubkeys(pubkeys).
        if (!aggregate.PublicKey.Compress().AsSpan().SequenceEqual(committee.AggregatePubkey.Bytes))
        {
            throw new InvalidDataException($"Anchor state at slot {slot} is refused: {field} aggregate_pubkey is not the aggregate of its pubkeys.");
        }
    }

    private static void ThrowIfUnsupportedFork(string? consensusVersion)
    {
        switch (consensusVersion?.ToLowerInvariant())
        {
            // When the header is missing, the state's slot selects its layout; the fork version inside it is checked after decoding.
            case null or "fulu" or "gloas":
                return;
            case "electra":
                throw new NotSupportedException("Electra checkpoint upgrade not implemented yet");
            default:
                throw new NotSupportedException($"Checkpoint state fork '{consensusVersion}' is not supported; the embedded beacon chain driver requires a Fulu or Gloas checkpoint.");
        }
    }

    /// <summary>
    /// Derives the anchor block root, filling only an unset header state root (consensus-specs beacon-chain.md process_slot).
    /// </summary>
    private static Hash256 ComputeAnchorBlockRoot(BeaconBlockHeader latestBlockHeader, Hash256 stateRoot)
    {
        BeaconBlockHeader.Merkleize(new BeaconBlockHeader
        {
            Slot = latestBlockHeader.Slot,
            ProposerIndex = latestBlockHeader.ProposerIndex,
            ParentRoot = latestBlockHeader.ParentRoot,
            StateRoot = latestBlockHeader.StateRoot == Hash256.Zero ? stateRoot : latestBlockHeader.StateRoot,
            BodyRoot = latestBlockHeader.BodyRoot,
        }, out UInt256 root);
        return new Hash256(root.ToLittleEndian());
    }

    private async Task<ForkedSignedBeaconBlock> GetAnchorBlockAsync(ForkedBeaconState state, Hash256 blockRoot, Hash256 stateRoot, CancellationToken cancellationToken)
    {
        byte[] blockSsz;
        if (config.CheckpointStateFile is { } stateFile)
        {
            string blockFile = Path.ChangeExtension(stateFile, ".block.ssz");
            if (!File.Exists(blockFile))
            {
                throw new InvalidDataException($"Checkpoint anchor block file is missing: {blockFile}.");
            }

            blockSsz = await File.ReadAllBytesAsync(blockFile, cancellationToken);
        }
        else
        {
            blockSsz = await RetryTransientAsync("anchor block download", async token =>
            {
                using HttpResponseMessage response = await GetOctetStreamAsync($"/eth/v2/beacon/blocks/{blockRoot}", token);
                (byte[] buffer, int length) = await ReadResponseBodyAsync(response, DefaultBlockBufferSize, token);
                try
                {
                    return buffer.AsSpan(0, length).ToArray();
                }
                finally
                {
                    BufferPool.Return(buffer);
                }
            }, cancellationToken);
        }

        ForkedSignedBeaconBlock block = SignedBeaconBlockCodec.Decode(blockSsz, spec);
        (Hash256 actualBlockRoot, Hash256 blockStateRoot) = block switch
        {
            // specs/gloas/fork.md upgrade_to_gloas keeps latest_block_header, so a state advanced past the upgrade still names its Fulu block.
            ForkedSignedBeaconBlock.OfFulu fulu when state is ForkedBeaconState.OfFulu || state.Slot > LatestBlockHeader(state).Slot => (SszRoots.HashTreeRoot(fulu.Block.Message!), fulu.Block.Message!.StateRoot!),
            ForkedSignedBeaconBlock.OfGloas gloas when state is ForkedBeaconState.OfGloas => (SszRoots.HashTreeRoot(gloas.Block.Message!), gloas.Block.Message!.StateRoot!),
            _ => throw new InvalidDataException($"Anchor block at slot {block.Slot} does not have the {state.Fork} shape of the checkpoint state at slot {state.Slot}."),
        };
        if (actualBlockRoot != blockRoot)
        {
            throw new InvalidDataException($"Anchor block root mismatch: expected {blockRoot}, got {actualBlockRoot}.");
        }

        if (blockStateRoot != stateRoot)
        {
            throw new InvalidDataException($"Anchor block state root mismatch: expected {stateRoot}, got {blockStateRoot}.");
        }

        return block;
    }

    private async Task<HttpResponseMessage> GetOctetStreamAsync(string path, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, $"{EffectiveCheckpointSyncUrl.TrimEnd('/')}{path}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(OctetStreamMediaType));
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (TaskCanceledException e) when (e.InnerException is TimeoutException && !cancellationToken.IsCancellationRequested)
        {
            // Without an inner exception: the logged cause is the innermost one, and here that is the aborted socket read.
            throw new IOException($"No response headers arrived for {ResponseHeadersTimeout.TotalSeconds:F0} s");
        }

        response.EnsureSuccessStatusCode();
        return response;
    }

    private void Persist(CheckpointAnchor anchor, ReadOnlySpan<byte> stateSsz, Checkpoint? weakSubjectivityCheckpoint)
    {
        store.PutState(anchor.BlockRoot, stateSsz);
        store.PutForkedBlock(anchor.BlockRoot, anchor.Block);

        // The anchor entry is written after the state and block: its presence marks a fully persisted checkpoint.
        store.SetAnchor(anchor.BlockRoot, LatestBlockHeader(anchor.State).Slot);
        if (weakSubjectivityCheckpoint is not null)
        {
            // Weak Subjectivity Sync Procedure (weak-subjectivity.md): record the proof only after its anchor is persisted.
            store.PutMetadata(BeaconChainMetadataKeys.WeakSubjectivityCheckpoint, EncodeCheckpointRecord(weakSubjectivityCheckpoint));
        }
    }

    private static Hash256 HashTreeRoot(ForkedBeaconState state) => state switch
    {
        ForkedBeaconState.OfFulu fulu => SszRoots.HashTreeRoot(fulu.State),
        ForkedBeaconState.OfGloas gloas => SszRoots.HashTreeRoot(gloas.State),
        _ => throw new NotSupportedException($"Unhandled beacon state shape {state.GetType().Name}"),
    };

    /// <summary>The operator's <see cref="IBeaconChainConfig.WeakSubjectivityCheckpoint"/>, or <c>null</c> when it is unset.</summary>
    /// <exception cref="InvalidConfigurationException">The value is not a 0x-prefixed 32-byte hex root, a colon and a decimal epoch.</exception>
    internal static Checkpoint? ParseWeakSubjectivityCheckpoint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // weak-subjectivity.md, Weak Subjectivity Sync Procedure: the input is block_root:epoch_number.
        string[] parts = value.Split(':');
        if (parts.Length != 2 || parts[0].Length != 2 + 2 * Hash256.Size || !parts[0].StartsWith("0x", StringComparison.Ordinal)
            || !IsHex(parts[0].AsSpan(2))
            || !ulong.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out ulong epoch))
        {
            throw new InvalidConfigurationException($"BeaconChain.WeakSubjectivityCheckpoint '{value}' is not block_root:epoch_number, a 0x-prefixed 32-byte hex root and a decimal epoch.", ExitCodes.ConflictingConfigurations);
        }

        return new Checkpoint { Root = new Hash256(parts[0]), Epoch = epoch };
    }

    /// <summary>Refuses a resumed anchor unless the configured weak subjectivity checkpoint was proven for this database or <paramref name="state"/> proves it.</summary>
    /// <remarks>
    /// The persisted anchor follows finality, so it is the checkpoint only until the first finalized block after it; the record
    /// written when the checkpoint was proven keeps it accepted. A checkpoint proven here is recorded the same way.
    /// </remarks>
    /// <exception cref="InvalidConfigurationException">The configured checkpoint is malformed.</exception>
    /// <exception cref="InvalidDataException">Neither the record nor <paramref name="state"/> proves the configured checkpoint.</exception>
    internal void ThrowIfResumedAnchorMissesWeakSubjectivityCheckpoint(ForkedBeaconState state, Hash256 anchorRoot)
    {
        if (ParseWeakSubjectivityCheckpoint(config.WeakSubjectivityCheckpoint) is not { } checkpoint)
        {
            return;
        }

        byte[] record = EncodeCheckpointRecord(checkpoint);
        if (store.GetMetadata(BeaconChainMetadataKeys.WeakSubjectivityCheckpoint) is { } proven && proven.AsSpan().SequenceEqual(record))
        {
            return;
        }

        if (!ProvesCheckpoint(state, anchorRoot, checkpoint))
        {
            throw new InvalidDataException($"The persisted anchor at slot {state.Slot} is not BeaconChain.WeakSubjectivityCheckpoint {config.WeakSubjectivityCheckpoint}, and this database recorded no other anchor as that checkpoint. Correct the checkpoint, or delete the beaconChain database to checkpoint-sync again.");
        }

        store.PutMetadata(BeaconChainMetadataKeys.WeakSubjectivityCheckpoint, record);
    }

    /// <summary>Whether <paramref name="anchorRoot"/>, the latest block of <paramref name="state"/>, is <paramref name="checkpoint"/>'s block at the start of its epoch.</summary>
    /// <remarks>
    /// Weak Subjectivity Sync Procedure (weak-subjectivity.md): the checkpoint root must authenticate the anchor state;
    /// an older root in provider-supplied <c>block_roots</c> cannot prove ancestry.
    /// </remarks>
    internal bool ProvesCheckpoint(ForkedBeaconState state, Hash256 anchorRoot, Checkpoint checkpoint) =>
        anchorRoot == checkpoint.Root
        && spec.GetEpoch(state.Slot) >= checkpoint.Epoch
        && LatestBlockHeader(state).Slot <= checkpoint.Epoch * spec.SlotsPerEpoch;

    private static byte[] EncodeCheckpointRecord(Checkpoint checkpoint)
    {
        byte[] record = new byte[Hash256.Size + sizeof(ulong)];
        checkpoint.Root!.Bytes.CopyTo(record);
        BinaryPrimitives.WriteUInt64BigEndian(record.AsSpan(Hash256.Size), checkpoint.Epoch);
        return record;
    }

    private static bool IsHex(ReadOnlySpan<char> value)
    {
        foreach (char digit in value)
        {
            if (!char.IsAsciiHexDigit(digit)) return false;
        }

        return true;
    }

    private static BeaconBlockHeader LatestBlockHeader(ForkedBeaconState state) => state switch
    {
        ForkedBeaconState.OfFulu fulu => fulu.State.LatestBlockHeader!,
        ForkedBeaconState.OfGloas gloas => gloas.State.LatestBlockHeader!,
        _ => throw new NotSupportedException($"Unhandled beacon state shape {state.GetType().Name}"),
    };

    private static int ValidatorCount(ForkedBeaconState state) => state switch
    {
        ForkedBeaconState.OfFulu fulu => fulu.State.Validators!.Length,
        ForkedBeaconState.OfGloas gloas => gloas.State.Validators!.Length,
        _ => throw new NotSupportedException($"Unhandled beacon state shape {state.GetType().Name}"),
    };

    public void Dispose() => _httpClient.Dispose();
}
