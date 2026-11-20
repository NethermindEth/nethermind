// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Arkiv.Sync;

/// <summary>
/// State sync for an Arkiv chain, in place of snap. The state a block commits to is GolemDB's, so that is what is
/// fetched: a peer's store copy (<c>arkiv/1</c>), taken at a block on this node's header chain past its sync pivot, and
/// installed only if its head commit is that block's — tagged with its hash, with the root its header carries. The local
/// account trie is then built from the accounts the store holds, and Nethermind goes on as after any state sync: it
/// executes the blocks from the next one on.
/// </summary>
/// <remarks>
/// The store's root and tag are checked, not every record against them: GolemDB validates a trie's branches as it reads
/// them, and a record a later block touches is checked against the root that block's header carries. arkiv-reth installs
/// its published snapshots without any check.
/// </remarks>
public sealed class ArkivStateSyncRunner(
    ArkivChain chain,
    ISyncModeSelector syncModeSelector,
    ISyncProgressResolver syncProgressResolver,
    ISyncPeerPool syncPeerPool,
    IBlockTree blockTree,
    INodeStorage nodeStorage,
    ITreeSyncStore treeSyncStore,
    ILogManager logManager) : IStateSyncRunner
{
    private const int ChunkSize = ArkivSyncProtocol.MaxChunk;
    // With MaxChunk, at most 2 MiB queued at the serving peer: under its 3 MB outbound buffer.
    private const int ChunksInFlight = 2;
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(2);

    private readonly ILogger _logger = logManager.GetClassLogger<ArkivStateSyncRunner>();
    private readonly ILogManager _logManager = logManager;

    public async Task Run(CancellationToken token)
    {
        try
        {
            if (syncProgressResolver.FindBestFullState() != 0)
            {
                if (_logger.IsInfo) _logger.Info("Arkiv state sync unnecessary - already have state.");
                return;
            }

            await syncModeSelector.WaitUntilMode(m => (m & SyncMode.StateNodes) != 0, token);
            if (_logger.IsInfo) _logger.Info("Arkiv state sync: fetching a peer's GolemDB store (arkiv/1).");
            DateTime lastWaitLog = DateTime.MinValue;
            while (!await TrySync(token))
            {
                if (DateTime.UtcNow - lastWaitLog > TimeSpan.FromSeconds(30) && _logger.IsInfo)
                {
                    int peers = ArkivPeers().Count;
                    _logger.Info(peers == 0
                        ? "Arkiv state sync: no peer serves Arkiv state (arkiv/1): add a Nethermind node running the Arkiv plugin with --Network.StaticPeers, or sync from genesis with --config rabdi_archive"
                        : $"Arkiv state sync: waiting for a peer serving a store copy past block {blockTree.SyncPivot.BlockNumber} ({peers} arkiv/1 peers)");
                    lastWaitLog = DateTime.UtcNow;
                }
                await Task.Delay(Retry, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Shutdown.
        }
    }

    public Task RunStateSyncRounds(CancellationToken token) => Task.CompletedTask;

    private List<IArkivSyncPeer> ArkivPeers()
    {
        List<IArkivSyncPeer> peers = [];
        foreach (PeerInfo peer in syncPeerPool.InitializedPeers)
        {
            if (peer.SyncPeer.TryGetSatelliteProtocol(ArkivSyncProtocol.Code, out IArkivSyncPeer arkiv)) peers.Add(arkiv);
        }
        return peers;
    }

    private async Task<bool> TrySync(CancellationToken token)
    {
        // Nethermind's forward sync starts after its sync pivot, so the state has to be at a block past it.
        ulong minNumber = blockTree.SyncPivot.BlockNumber + 1;
        foreach (IArkivSyncPeer peer in ArkivPeers())
        {
            StoreManifestMessage manifest;
            try
            {
                manifest = await peer.GetStoreManifest(minNumber, token);
            }
            catch (Exception e) when (e is not OperationCanceledException || !token.IsCancellationRequested)
            {
                if (_logger.IsDebug) _logger.Debug($"Arkiv state sync: no manifest from {peer}: {e.Message}");
                continue;
            }
            if (manifest.Number < minNumber) continue;

            // The copy must be of a block on this node's chain: its header is what the store is checked against.
            BlockHeader? header = blockTree.FindHeader(manifest.Hash, BlockTreeLookupOptions.None);
            if (header is null || header.Number != manifest.Number)
            {
                if (_logger.IsDebug) _logger.Debug($"Arkiv state sync: a peer serves block {manifest.Number} ({manifest.Hash}), whose header this node does not have yet");
                continue;
            }

            string incoming = chain.StoreDirectory + ".incoming";
            try
            {
                if (Directory.Exists(incoming)) Directory.Delete(incoming, recursive: true);
                if (!await Download(peer, manifest, incoming, token)) continue;
                Stopwatch install = Stopwatch.StartNew();
                IReadOnlyList<(Address Address, UInt256 Balance, ulong Nonce)> accounts = chain.InstallStore(incoming, header);
                Hash256 trieRoot = BuildAccountTrie(accounts);
                chain.MapRoot(header.StateRoot!, trieRoot);
                treeSyncStore.FinalizeSync(header);
                if (_logger.IsInfo) _logger.Info($"Arkiv state sync finished at block {header.ToString(BlockHeader.Format.Short)}: GolemDB root {header.StateRoot} verified, {accounts.Count} accounts in the local trie (root {trieRoot}), installed in {install.Elapsed.TotalSeconds:N1} s");
                return true;
            }
            catch (Exception e) when (e is not OperationCanceledException || !token.IsCancellationRequested)
            {
                if (_logger.IsWarn) _logger.Warn($"Arkiv state sync: the store copy at block {manifest.Number} ({manifest.Hash}) was not usable: {e.Message}");
            }
            finally
            {
                if (Directory.Exists(incoming)) Directory.Delete(incoming, recursive: true);
            }
        }
        return false;
    }

    /// <returns>False when the peer stopped serving the copy part-way (a newer one replaced it): ask again.</returns>
    private async Task<bool> Download(IArkivSyncPeer peer, StoreManifestMessage manifest, string directory, CancellationToken token)
    {
        long total = manifest.Files.Sum(f => f.Size);
        if (_logger.IsInfo) _logger.Info($"Arkiv state sync: fetching the store copy at block {manifest.Number} ({manifest.Hash}), {manifest.Files.Count} files, {total / (1024 * 1024)} MiB");
        Stopwatch elapsed = Stopwatch.StartNew();
        long fetched = 0;
        TimeSpan lastLog = TimeSpan.Zero;

        for (int file = 0; file < manifest.Files.Count; file++)
        {
            (string path, long size) = manifest.Files[file];
            string target = Path.GetFullPath(Path.Combine(directory, path));
            // A peer names the files; none may land outside the directory.
            if (!target.StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, StringComparison.Ordinal) || size < 0)
            {
                throw new InvalidDataException($"the manifest names an invalid file: {path} ({size} bytes)");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using FileStream stream = new(target, FileMode.Create, FileAccess.Write, FileShare.None);
            stream.SetLength(size);

            Queue<long> offsets = new();
            for (long offset = 0; offset < size; offset += ChunkSize) offsets.Enqueue(offset);
            List<Task<(long Offset, byte[] Data)>> inFlight = [];
            while (offsets.Count > 0 || inFlight.Count > 0)
            {
                while (offsets.Count > 0 && inFlight.Count < ChunksInFlight)
                {
                    long offset = offsets.Dequeue();
                    int length = (int)Math.Min(ChunkSize, size - offset);
                    int index = file;
                    inFlight.Add(Task.Run(async () => (offset, await peer.GetStoreChunk(manifest.Hash, index, offset, length, token)), token));
                }
                Task<(long Offset, byte[] Data)> done = await Task.WhenAny(inFlight);
                inFlight.Remove(done);
                (long at, byte[] data) = await done;
                int expected = (int)Math.Min(ChunkSize, size - at);
                if (data.Length != expected)
                {
                    if (data.Length == 0) return false;
                    throw new InvalidDataException($"chunk at {at} of {path} has {data.Length} bytes, {expected} expected");
                }
                stream.Seek(at, SeekOrigin.Begin);
                await stream.WriteAsync(data, token);
                fetched += data.Length;
                if (elapsed.Elapsed - lastLog > TimeSpan.FromSeconds(10) && _logger.IsInfo)
                {
                    lastLog = elapsed.Elapsed;
                    _logger.Info($"Arkiv state sync: {fetched / (1024 * 1024)} / {total / (1024 * 1024)} MiB ({fetched / Math.Max(1, elapsed.Elapsed.TotalSeconds) / (1024 * 1024):N0} MiB/s)");
                }
            }
        }
        if (_logger.IsInfo) _logger.Info($"Arkiv state sync: fetched {total / (1024 * 1024)} MiB in {elapsed.Elapsed.TotalSeconds:N1} s");
        return true;
    }

    /// <summary>
    /// The local account trie at the synced block: the genesis trie (whose predeploys keep their code and storage) with
    /// every account's balance and nonce as GolemDB holds them, written straight to the node store.
    /// </summary>
    private Hash256 BuildAccountTrie(IReadOnlyList<(Address Address, UInt256 Balance, ulong Nonce)> accounts)
    {
        Hash256 genesisTrie = chain.TrieRootOf(chain.GenesisRoot) ?? throw new InvalidOperationException("Arkiv: the genesis account trie's root is not recorded");
        StateTree tree = new(new RawScopedTrieStore(nodeStorage), _logManager) { RootHash = genesisTrie };
        foreach ((Address address, UInt256 balance, ulong nonce) in accounts)
        {
            Account current = tree.Get(address) ?? Account.TotallyEmpty;
            tree.Set(address, current.WithChangedBalance(balance).WithChangedNonce(nonce));
        }
        tree.Commit();
        nodeStorage.Flush(onlyWal: false);
        return tree.RootHash;
    }
}
