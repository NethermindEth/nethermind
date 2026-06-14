// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.BeaconChain.Sync;

/// <summary>The verified finalized checkpoint the driver bootstraps from.</summary>
/// <param name="Block">
/// <c>null</c> only when bootstrapping from a local state file without a sibling block file, in
/// which case the anchor is synthesized from <c>state.LatestBlockHeader</c> — a testing-only mode.
/// </param>
public record CheckpointAnchor(BeaconStateFulu State, SignedBeaconBlock? Block, Hash256 BlockRoot, Hash256 StateRoot);

/// <summary>Bootstraps the beacon chain from a finalized checkpoint state and block.</summary>
/// <remarks>
/// Downloads the finalized state from the configured beacon API (or reads it from
/// <see cref="IBeaconChainConfig.CheckpointStateFile"/>), recomputes its hash tree root, derives
/// the anchor block root from <c>state.LatestBlockHeader</c>, fetches and cross-verifies the
/// anchor block, and persists everything to the <see cref="BeaconChainStore"/>. Only Fulu states
/// are supported.
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

    private readonly ILogger _logger = logManager.GetClassLogger<CheckpointSync>();
    private readonly HttpClient _httpClient = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
    {
        // The finalized state is ~300 MB; allow for slow providers.
        Timeout = TimeSpan.FromMinutes(10),
    };

    public async Task<CheckpointAnchor> RunAsync(CancellationToken cancellationToken)
    {
        (byte[] buffer, int length) = config.CheckpointStateFile is { } stateFile
            ? await ReadStateFileAsync(stateFile, cancellationToken)
            : await DownloadStateAsync(cancellationToken);

        try
        {
            BeaconStateFulu state = DecodeState(buffer.AsSpan(0, length));

            Stopwatch stopwatch = Stopwatch.StartNew();
            Hash256 stateRoot = HashTreeRoot(state);
            if (_logger.IsInfo) _logger.Info($"Computed checkpoint state root {stateRoot} ({state.Validators!.Length} validators) in {stopwatch.Elapsed.TotalSeconds:F1} s");

            Hash256 blockRoot = ComputeAnchorBlockRoot(state.LatestBlockHeader!, stateRoot);
            SignedBeaconBlock? block = await GetAnchorBlockAsync(blockRoot, stateRoot, cancellationToken);

            CheckpointAnchor anchor = new(state, block, blockRoot, stateRoot);
            Persist(anchor, buffer.AsSpan(0, length));
            if (_logger.IsInfo) _logger.Info($"Checkpoint sync complete: anchor block {blockRoot} at slot {state.LatestBlockHeader!.Slot}");
            return anchor;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task<(byte[] Buffer, int Length)> DownloadStateAsync(CancellationToken cancellationToken)
    {
        if (_logger.IsInfo) _logger.Info($"Downloading finalized beacon state from {config.CheckpointSyncUrl}");
        Stopwatch stopwatch = Stopwatch.StartNew();

        using HttpResponseMessage response = await GetOctetStreamAsync("/eth/v2/debug/beacon/states/finalized", cancellationToken);
        ThrowIfUnsupportedFork(response.Headers.TryGetValues(ConsensusVersionHeader, out IEnumerable<string>? values) ? values.FirstOrDefault() : null);

        await using Stream content = await response.Content.ReadAsStreamAsync(cancellationToken);
        (byte[] buffer, int length) = await ReadToPooledBufferAsync(content, response.Content.Headers.ContentLength, cancellationToken);

        if (_logger.IsInfo) _logger.Info($"Downloaded finalized beacon state: {length / (1024.0 * 1024.0):F1} MB in {stopwatch.Elapsed.TotalSeconds:F1} s");
        return (buffer, length);
    }

    private static async Task<(byte[] Buffer, int Length)> ReadStateFileAsync(string stateFile, CancellationToken cancellationToken)
    {
        await using FileStream content = File.OpenRead(stateFile);
        return await ReadToPooledBufferAsync(content, content.Length, cancellationToken);
    }

    private static async Task<(byte[] Buffer, int Length)> ReadToPooledBufferAsync(Stream content, long? expectedLength, CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent((int)(expectedLength ?? DefaultStateBufferSize));
        int length = 0;
        while (true)
        {
            if (length == buffer.Length)
            {
                byte[] grown = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                buffer.CopyTo(grown, 0);
                ArrayPool<byte>.Shared.Return(buffer);
                buffer = grown;
            }

            int read = await content.ReadAsync(buffer.AsMemory(length), cancellationToken);
            if (read == 0)
            {
                return (buffer, length);
            }

            length += read;
        }
    }

    private BeaconStateFulu DecodeState(ReadOnlySpan<byte> sszBytes)
    {
        BeaconStateFulu.Decode(sszBytes, out BeaconStateFulu state);
        ThrowIfUnsupportedFork(state);
        return state;
    }

    /// <summary>Maps the state's fork version onto the spec schedule and refuses anything that is not Fulu.</summary>
    private void ThrowIfUnsupportedFork(BeaconStateFulu state)
    {
        byte[] currentVersion = state.Fork!.CurrentVersion!;
        foreach (ForkScheduleEntry entry in spec.Forks)
        {
            if (entry.Version.AsSpan().SequenceEqual(currentVersion))
            {
                if (entry.Epoch < spec.ElectraForkEpoch)
                {
                    throw new NotSupportedException($"Checkpoint state fork version {currentVersion.ToHexString(true)} predates Electra; the embedded beacon chain driver requires a Fulu checkpoint.");
                }

                if (entry.Epoch < spec.FuluForkEpoch)
                {
                    throw new NotSupportedException("Electra checkpoint upgrade not implemented yet");
                }

                return;
            }
        }

        throw new NotSupportedException($"Checkpoint state has unknown fork version {currentVersion.ToHexString(true)}.");
    }

    private static void ThrowIfUnsupportedFork(string? consensusVersion)
    {
        switch (consensusVersion?.ToLowerInvariant())
        {
            // When the header is missing, optimistically decode as Fulu; the fork version inside the state is checked after decoding.
            case null or "fulu":
                return;
            case "electra":
                throw new NotSupportedException("Electra checkpoint upgrade not implemented yet");
            default:
                throw new NotSupportedException($"Checkpoint state fork '{consensusVersion}' is not supported; the embedded beacon chain driver requires a Fulu checkpoint.");
        }
    }

    /// <summary>
    /// Derives the anchor block root from the state's latest block header, whose
    /// <c>state_root</c> is zeroed in the state and must be patched in first.
    /// </summary>
    private static Hash256 ComputeAnchorBlockRoot(BeaconBlockHeader latestBlockHeader, Hash256 stateRoot)
    {
        BeaconBlockHeader.Merkleize(new BeaconBlockHeader
        {
            Slot = latestBlockHeader.Slot,
            ProposerIndex = latestBlockHeader.ProposerIndex,
            ParentRoot = latestBlockHeader.ParentRoot,
            StateRoot = stateRoot,
            BodyRoot = latestBlockHeader.BodyRoot,
        }, out UInt256 root);
        return new Hash256(root.ToLittleEndian());
    }

    private async Task<SignedBeaconBlock?> GetAnchorBlockAsync(Hash256 blockRoot, Hash256 stateRoot, CancellationToken cancellationToken)
    {
        byte[] blockSsz;
        if (config.CheckpointStateFile is { } stateFile)
        {
            string blockFile = Path.ChangeExtension(stateFile, ".block.ssz");
            if (!File.Exists(blockFile))
            {
                return null;
            }

            blockSsz = await File.ReadAllBytesAsync(blockFile, cancellationToken);
        }
        else
        {
            using HttpResponseMessage response = await GetOctetStreamAsync($"/eth/v2/beacon/blocks/{blockRoot}", cancellationToken);
            blockSsz = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        }

        SignedBeaconBlock.Decode(blockSsz, out SignedBeaconBlock block);

        BeaconBlock.Merkleize(block.Message!, out UInt256 root);
        Hash256 actualBlockRoot = new(root.ToLittleEndian());
        if (actualBlockRoot != blockRoot)
        {
            throw new InvalidDataException($"Anchor block root mismatch: expected {blockRoot}, got {actualBlockRoot}.");
        }

        if (block.Message!.StateRoot != stateRoot)
        {
            throw new InvalidDataException($"Anchor block state root mismatch: expected {stateRoot}, got {block.Message.StateRoot}.");
        }

        return block;
    }

    private async Task<HttpResponseMessage> GetOctetStreamAsync(string path, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, $"{config.CheckpointSyncUrl.TrimEnd('/')}{path}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(OctetStreamMediaType));
        HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return response;
    }

    private void Persist(CheckpointAnchor anchor, ReadOnlySpan<byte> stateSsz)
    {
        store.PutState(anchor.BlockRoot, stateSsz);
        if (anchor.Block is not null)
        {
            store.PutBlock(anchor.BlockRoot, anchor.Block);
        }

        store.PutMetadata(BeaconChainMetadataKeys.GenesisValidatorsRoot, anchor.State.GenesisValidatorsRoot!.BytesToArray());
        // The anchor entry is written last: its presence marks a fully persisted checkpoint.
        store.SetAnchor(anchor.BlockRoot, anchor.State.LatestBlockHeader!.Slot);
    }

    private static Hash256 HashTreeRoot(BeaconStateFulu state)
    {
        BeaconStateFulu.Merkleize(state, out UInt256 root);
        return new Hash256(root.ToLittleEndian());
    }

    public void Dispose() => _httpClient.Dispose();
}
