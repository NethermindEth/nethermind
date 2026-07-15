// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Autofac.Features.AttributeFilters;
using Microsoft.Win32.SafeHandles;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.Eip7928;
using Nethermind.State;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.SnapSync;

namespace Nethermind.Synchronization.FastSync;

public class StateSyncRunner(
    ISnapSyncRunner snapSyncRunner,
    IBalHealing balHealing,
    IStateSyncPivot stateSyncPivot,
    TreeSync treeSync,
    SimpleDispatcher<StateSyncBatch> stateSyncDispatcher,
    ISyncConfig syncConfig,
    ISyncModeSelector syncModeSelector,
    ISyncProgressResolver syncProgressResolver,
    IBeaconSyncStrategy beaconSyncStrategy,
    ISyncPeerPool syncPeerPool,
    IBlockTree blockTree,
    IBlockAccessListStore blockAccessListStore,
    [KeyFilter(DbNames.State)] ITunableDb? stateDb,
    [KeyFilter(DbNames.Code)] ITunableDb? codeDb,
    ILogManager logManager,
    IVerifyTrieStarter? verifyTrieStarter = null) : IStateSyncRunner
{
    private readonly ILogger _logger = logManager.GetClassLogger<StateSyncRunner>();

    public async Task Run(CancellationToken token)
    {
        try
        {
            if (syncProgressResolver.FindBestFullState() != 0)
            {
                if (_logger.IsInfo) _logger.Info("State sync unnecessary - already have state.");
                return;
            }

            await StateSyncPrecursorWait(token);
            TuneStateDb(syncConfig.TuneDbMode);

            RecordedBalStore recordedBalStore = new(logManager);
            recordedBalStore.Insert(0, new GeneratedBlockAccessList());
     

            try
            {
                if (syncConfig.SnapSync)
                {
                    BlockHeader? firstPivot = stateSyncPivot.GetPivotHeader();
                    if (_logger.IsInfo) _logger.Info("Starting snap sync. at pivot block " + (stateSyncPivot.GetPivotHeader()?.Number.ToString() ?? "<unknown>"));
                    await snapSyncRunner.Run(token);
                    if (_logger.IsInfo) _logger.Info("Snap sync completed. at pivot block " + (stateSyncPivot.GetPivotHeader()?.Number.ToString() ?? "<unknown>"));
                        
                    if (firstPivot is not null && await RunBalHealing(firstPivot, token))
                        return;
                }

                await RunStateSyncRounds(token);

                if (syncConfig.StaticSnapPivot && _logger.IsInfo)
                    _logger.Info($"StaticSnapPivot: state sync complete at block {syncConfig.PivotNumber} - node is idle (no further sync without a consensus client). Set Sync.ExitOnSynced=true to exit on completion.");
            }
            finally
            {
                // Skip on shutdown so we don't touch DBs that may already be disposed.
                if (!token.IsCancellationRequested) TuneStateDb(ITunableDb.TuneType.Default);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Clean shutdown — swallow so Synchronizer doesn't log "State sync failed".
        }
    }

    public async Task<bool> RunBalHealing(BlockHeader firstPivot, CancellationToken token)
    {
        stateSyncPivot.UpdateHeaderForcefully();
        BlockHeader? lastPivot = stateSyncPivot.GetPivotHeader();

        if (lastPivot is null)
        {
            if (_logger.IsInfo) _logger.Info("BAL healing skipped - no pivot header available.");
            return false;
        }

        bool healingComplete = await balHealing.Run(firstPivot, lastPivot, stateSyncPivot.UpdatedStorages, token);

        if (!healingComplete)
        {
            if (_logger.IsError) _logger.Error("BAL healing unavailable or failed — falling back to traditional state sync.");
            return false;
        }

        while (true)
        {
            firstPivot = lastPivot;

            stateSyncPivot.UpdateHeaderForcefully();
            lastPivot = stateSyncPivot.GetPivotHeader();

            if(firstPivot.Number == lastPivot?.Number)
            {
                if (_logger.IsInfo) _logger.Info($"BAL healing complete - no new pivot header available after block {firstPivot.Number}.");
                await Task.Delay(1000, token);
                continue;
            }
    
            if (_logger.IsInfo) _logger.Info($"BAL healing: first pivot {firstPivot.Number}, last pivot {lastPivot?.Number.ToString() ?? "<unknown>"}");
            bool healingComplete2 = await balHealing.Run(firstPivot, lastPivot, stateSyncPivot.UpdatedStorages, token);
                
            if (!healingComplete2)
            {
                if (_logger.IsError) _logger.Error("BAL healing unavailable or failed — falling back to traditional state sync.");
                return false;
            }

        }

        // if (_logger.IsInfo) _logger.Info("BAL healing completed — skipping traditional state sync.");

        // if (syncConfig.VerifyTrieOnStateSyncFinished)
        //     verifyTrieStarter?.TryStartVerifyTrie(lastPivot);

        // return true;
    }


    public async void GetBALS(ulong start)
    {
        RecordedBalStore recordedBalStore = new(logManager);
        ulong blockNumber = start;
        int tryCount = 0;
        while (true)
        {   
            BlockHeader? header = blockTree.FindHeader(blockNumber);
            while (header is null)
            {
            
                if(tryCount == 5)
                    break; 

                if (_logger.IsWarn) _logger.Warn($"Will retry to find header for block number {blockNumber} after 5 seconds.");
                tryCount += 1;
                await Task.Delay(5000);
                
                header = blockTree.FindHeader(blockNumber);
            }

            if(header is null)
            {
                if (_logger.IsWarn) _logger.Warn($"Header not found for block number {blockNumber}.");
                break;
            }


            ReadOnlyBlockAccessList? bal = recordedBalStore.Get(blockNumber);

            if (bal is null)
            {
                if (_logger.IsWarn) _logger.Warn($"Bal not found for block number {blockNumber}.");
                break;
            }

            blockAccessListStore.Insert(header.Number, header.Hash, bal);
            if(_logger.IsInfo) _logger.Info($"Inserted bal for block number {blockNumber}.");

            blockNumber++;
        }                    
    }

    public async Task RunStateSyncRounds(CancellationToken token)
    {
        if (_logger.IsInfo) _logger.Info("Starting state sync.");

        if (_logger.IsInfo)
        {
            _logger.Info($"Heal - {stateSyncPivot.UpdatedStorages.Count} accounts to heal (UpdatedStorages):");
            foreach (Hash256 accountToHeal in stateSyncPivot.UpdatedStorages)
                _logger.Info($"Heal - account to heal: {accountToHeal}");
        }

        BlockHeader? finalPivot = null;

        while (!token.IsCancellationRequested)
        {
            // Yield between rounds when the mode selector has moved away from StateNodes
            // (e.g. beacon control, UpdatingPivot, fast-sync re-entry) or when we've drifted
            // away from head, so those phases can claim peers. Returns immediately if already
            // in StateNodes mode and close to head.
            await StateSyncPrecursorWait(token);

            BlockHeader? roundPivot = treeSync.ResetStateRootToBestSuggested();
            if (roundPivot is null)
            {
                // Pivot not known yet — wait and retry. StateSyncPrecursorWait can return
                await Task.Delay(1000, token);
                continue;
                // immediately, so without this we'd spin tightly.
            }

            await stateSyncDispatcher.Run(token);

            // If sync completed in this round, the pivot it committed against is roundPivot.
            // Capturing here avoids re-reading GetPivotHeader() (mutating) for FinalizeSync.
            if (treeSync.CanFinalize(roundPivot))
            {
                finalPivot = roundPivot;
                break;
            }
        }

        if (finalPivot is null) return;

        if (_logger.IsInfo) _logger.Info($"STATE SYNC FINISHED:{Metrics.StateSyncRequests}, {Metrics.SyncedStateTrieNodes}");

        treeSync.VerifyPostSyncCleanUp();
        treeSync.FinalizeSync(finalPivot);

        if (syncConfig.VerifyTrieOnStateSyncFinished)
            verifyTrieStarter?.TryStartVerifyTrie(finalPivot);
    }

    private void TuneStateDb(ITunableDb.TuneType tuneType)
    {
        stateDb?.Tune(tuneType);
        codeDb?.Tune(tuneType);
    }

    /// <summary>
    /// DEBUG-ONLY. Copies the live DB directory to a sibling <c>&lt;BaseDbPath&gt;_snapshot</c> dir right
    /// after snap sync so post-snap-sync behaviour can be tested without re-downloading state each time.
    /// The source DB dir is auto-detected from the state DB.
    /// </summary>
    /// <remarks>
    /// Flushes the state and code DBs before copying to make the snapshot as consistent as possible,
    /// but this is a plain file copy of a live RocksDB directory — it is a dev convenience, not a
    /// guaranteed-consistent backup. Stop the node before relying on the copy for other column families.
    /// </remarks>
    private void BackupDatabaseForDebug()
    {
        try
        {
            // The state and code DBs live under <BaseDbPath> (e.g. .../state, .../code), but their
            // exact depth varies (flat/columns layouts nest deeper). Their common ancestor directory
            // is <BaseDbPath> itself — the whole DB dir to back up.
            string? src = CommonAncestorDir(TryResolveDbPath(stateDb), TryResolveDbPath(codeDb));
            if (string.IsNullOrEmpty(src))
            {
                if (_logger.IsError) _logger.Error("DEBUG: could not resolve DB path for backup; skipping.");
                return;
            }

            string dst = Path.TrimEndingDirectorySeparator(src) + "_snapshot";

            if (_logger.IsInfo) _logger.Info("DEBUG: flushing state/code DBs before backup.");
            (stateDb as IDbMeta)?.Flush();
            (codeDb as IDbMeta)?.Flush();

            if (_logger.IsInfo) _logger.Info($"DEBUG: backing up DB from '{src}' to '{dst}'.");
            CopyDirectory(src, dst);
            if (_logger.IsInfo) _logger.Info($"DEBUG: DB backup completed at '{dst}'.");
        }
        catch (Exception ex)
        {
            if (_logger.IsError) _logger.Error("DEBUG: DB backup failed.", ex);
        }
    }

    /// <summary>
    /// Digs the on-disk path out of a (possibly wrapped) RocksDB instance via reflection, unwrapping
    /// known decorators (<c>FullPruningDb</c>, the EOA-compressing wrapper) to reach <c>DbOnTheRocks._fullPath</c>.
    /// </summary>
    private static string? TryResolveDbPath(object? db, int depth = 0)
    {
        if (db is null || depth > 4) return null;

        for (Type? t = db.GetType(); t is not null; t = t.BaseType)
        {
            FieldInfo? pathField = t.GetField("_fullPath", BindingFlags.Instance | BindingFlags.NonPublic);
            if (pathField?.GetValue(db) is string path && !string.IsNullOrEmpty(path))
                return path;
        }

        // Not a raw DbOnTheRocks — descend into any wrapped inner db field.
        foreach (FieldInfo field in db.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
        {
            if (!typeof(IDb).IsAssignableFrom(field.FieldType) && !typeof(ITunableDb).IsAssignableFrom(field.FieldType))
                continue;
            string? inner = TryResolveDbPath(field.GetValue(db), depth + 1);
            if (inner is not null) return inner;
        }

        return null;
    }

    /// <summary>
    /// Returns the deepest directory that is an ancestor of both paths — i.e. their longest common
    /// path-segment prefix. Used to recover the DB base dir from two DB paths that nest to different depths.
    /// </summary>
    private static string? CommonAncestorDir(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a)) return string.IsNullOrEmpty(b) ? null : Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(b));
        if (string.IsNullOrEmpty(b)) return Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(a));

        string[] segmentsA = Path.TrimEndingDirectorySeparator(a).Split(Path.DirectorySeparatorChar);
        string[] segmentsB = Path.TrimEndingDirectorySeparator(b).Split(Path.DirectorySeparatorChar);

        int i = 0;
        int max = Math.Min(segmentsA.Length, segmentsB.Length);
        while (i < max && segmentsA[i] == segmentsB[i]) i++;

        return string.Join(Path.DirectorySeparatorChar, segmentsA, 0, i);
    }

    /// <summary>
    /// Copies <paramref name="sourceDir"/> to <paramref name="destinationDir"/> using hardlinks
    /// (<c>cp -al</c>) so the snapshot is near-instant and shares blocks with the source on the same
    /// filesystem. RocksDB SST files are immutable once written, so hardlinking them is safe.
    /// Falls back to a plain recursive byte copy if <c>cp</c> is unavailable or fails.
    /// </summary>
    private void CopyDirectory(string sourceDir, string destinationDir)
    {
        if (Directory.Exists(destinationDir))
            Directory.Delete(destinationDir, recursive: true);

        try
        {
            ProcessStartInfo psi = new("cp")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-al");
            psi.ArgumentList.Add(sourceDir);
            psi.ArgumentList.Add(destinationDir);

            using Process? cp = Process.Start(psi);
            if (cp is not null)
            {
                string error = cp.StandardError.ReadToEnd();
                cp.WaitForExit();
                if (cp.ExitCode == 0) return;
                if (_logger.IsWarn) _logger.Warn($"DEBUG: 'cp -al' failed (exit {cp.ExitCode}): {error}. Falling back to byte copy.");
            }
        }
        catch (Exception ex)
        {
            if (_logger.IsWarn) _logger.Warn($"DEBUG: 'cp -al' unavailable ({ex.Message}). Falling back to byte copy.");
        }

        CopyDirectoryRecursive(sourceDir, destinationDir);
    }

    private static void CopyDirectoryRecursive(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);

        foreach (string file in Directory.EnumerateFiles(sourceDir))
            File.Copy(file, Path.Combine(destinationDir, Path.GetFileName(file)), overwrite: true);

        foreach (string subDir in Directory.EnumerateDirectories(sourceDir))
            CopyDirectoryRecursive(subDir, Path.Combine(destinationDir, Path.GetFileName(subDir)));
    }

    private async Task StateSyncPrecursorWait(CancellationToken token)
    {
        await syncModeSelector.WaitUntilMode(m => (m & SyncMode.StateNodes) != 0, token);

        if (syncConfig.StaticSnapPivot) return;

        ulong totalSyncLag = syncConfig.StateMinDistanceFromHead + syncConfig.HeaderStateDistance;

        while (!token.IsCancellationRequested)
        {
            ulong header = syncProgressResolver.FindBestHeader();
            ulong peerBlock = 0;
            foreach (PeerInfo p in syncPeerPool.InitializedPeers)
            {
                ulong peerHeadNumber = p.HeadNumber;
                if (peerHeadNumber > peerBlock) peerBlock = peerHeadNumber;
            }
            ulong targetBlock = beaconSyncStrategy.GetTargetBlockHeight() ?? peerBlock;

            if (targetBlock >= header && (targetBlock - header) <= totalSyncLag)
                return;

            await Task.Delay(1000, token);
        }
    }
}


public class RecordedBalStore(ILogManager logManager)
{
    private static readonly BlockAccessListDecoder BalDecoder = BlockAccessListDecoder.Instance;
    private readonly ILogger _logger = logManager.GetClassLogger<RecordedBalStore>();
    private readonly SlotStore _store = new("~/BAL", "bal");

    public void Dispose() => _store.Dispose();

    public void Insert(ulong number, GeneratedBlockAccessList bal)
    {
        using ArrayPoolSpan<byte> rlp = BlockAccessListDecoder.EncodeToArrayPoolSpan(bal);
        if (!_store.Write(number, rlp))
            if (_logger.IsDebug) _logger.Debug($"BAL slot for block {number} already filled; skipping.");
    }

    public ReadOnlyBlockAccessList? Get(ulong blockNumber)
    {
        ReadState state = new() { Logger = _logger, BlockNumber = blockNumber };
        _store.TryRead(blockNumber, static (data, s) =>
        {
            try { s.Value = BalDecoder.Decode(data); }
            catch (RlpException ex) { s.Logger.Warn($"Corrupt BAL slot for block {s.BlockNumber}: {ex.Message}"); }
        }, state);
        return state.Value;
    }

    private sealed class ReadState
    {
        public ReadOnlyBlockAccessList? Value;
        public ILogger Logger;
        public ulong BlockNumber;
    }
}


public class SlotStore(string directory, string extension = "bin") : IDisposable
{
    private SlotFile? _file;
    private ulong? _fileEra;
    private readonly Lock _lock = new();

    private string FilePath(ulong era) => Path.Combine(directory, $"{era:D8}.{extension}");

    public bool TryRead<TArg>(ulong blockNumber, ReadOnlySpanAction<byte, TArg> action, TArg arg)
    {
        ulong era = blockNumber / SlotFile.SlotsPerFile;
        int slot = (int)(blockNumber % SlotFile.SlotsPerFile);
        lock (_lock)
        {
            if (_fileEra != era)
            {
                string path = FilePath(era);
                if (!File.Exists(path)) return false;
                _file?.Dispose();
                _file = new SlotFile(path);
                _fileEra = era;
            }
            return _file!.TryRead(slot, action, arg);
        }
    }

    public bool Write(ulong blockNumber, ReadOnlySpan<byte> data)
    {
        ulong era = blockNumber / SlotFile.SlotsPerFile;
        int slot = (int)(blockNumber % SlotFile.SlotsPerFile);
        lock (_lock)
        {
            if (_fileEra != era)
            {
                _file?.Dispose();
                Directory.CreateDirectory(directory);
                _file = new SlotFile(FilePath(era));
                _fileEra = era;
            }
            return _file!.TryWrite(slot, data);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _file?.Dispose();
            _file = null;
            _fileEra = null;
        }
    }
}


/// <summary>
/// Wraps a single era file: one <see cref="SafeFileHandle"/> kept open for the store's lifetime.
/// The 65536-byte slot-index header is cached in memory; reads use <c>RandomAccess</c> positioned I/O
/// (no locking required). Writes are serialized by a per-instance lock.
/// </summary>
public sealed class SlotFile : IDisposable
{
    public const int SlotsPerFile = 8192;
    private const int HeaderSize = SlotsPerFile * 8; // 65536 bytes

    private readonly SafeFileHandle _handle;
    private readonly byte[] _header = new byte[HeaderSize];
    private readonly Lock _writeLock = new();
    private long _length;

    public SlotFile(string path)
    {
        _handle = File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        _length = RandomAccess.GetLength(_handle);
        if (_length == 0)
        {
            RandomAccess.Write(_handle, _header, 0);
            _length = HeaderSize;
        }
        else
        {
            RandomAccess.Read(_handle, _header, 0);
        }
    }

    public bool TryRead<TArg>(int slot, ReadOnlySpanAction<byte, TArg> action, TArg arg)
    {
        uint offset, size;
        lock (_writeLock)
        {
            ReadOnlySpan<byte> entry = _header.AsSpan(slot * 8, 8);
            offset = BinaryPrimitives.ReadUInt32BigEndian(entry);
            if (offset == 0) return false;
            size = BinaryPrimitives.ReadUInt32BigEndian(entry[4..]);
        }
        if (size == 0 || size > 64 * MemorySizes.MiB) return false;

        byte[] rented = ArrayPool<byte>.Shared.Rent((int)size);
        try
        {
            RandomAccess.Read(_handle, rented.AsSpan(0, (int)size), offset);
            action(new ReadOnlySpan<byte>(rented, 0, (int)size), arg);
            return true;
        }
        finally { ArrayPool<byte>.Shared.Return(rented); }
    }

    public bool TryWrite(int slot, ReadOnlySpan<byte> data)
    {
        lock (_writeLock)
        {
            if (BinaryPrimitives.ReadUInt32BigEndian(_header.AsSpan(slot * 8, 8)) != 0) return false;

            if (_length > uint.MaxValue)
                throw new InvalidOperationException($"Era file exceeded 4 GB limit at offset {_length}.");
            uint offset = (uint)_length;
            RandomAccess.Write(_handle, data, offset);
            _length += data.Length;

            Span<byte> entry = _header.AsSpan(slot * 8, 8);
            BinaryPrimitives.WriteUInt32BigEndian(entry, offset);
            BinaryPrimitives.WriteUInt32BigEndian(entry[4..], (uint)data.Length);
            RandomAccess.Write(_handle, entry, slot * 8);
            return true;
        }
    }

    public void Dispose() => _handle.Dispose();
}
