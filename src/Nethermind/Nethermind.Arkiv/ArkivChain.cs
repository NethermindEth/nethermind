// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Arkiv.Execution;
using Nethermind.Arkiv.Native;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Arkiv;

/// <summary>
/// The node's one handle on Arkiv's state: the native GolemDB store, executed and committed block by block.
/// </summary>
/// <remarks>
/// <para>
/// GolemDB has no rewind and reads at its head only, so Nethermind and the store must agree on which block the store
/// holds. Three things keep them in step:
/// </para>
/// <list type="bullet">
/// <item>Each committed block's native result is kept under its hash. A block Nethermind processes again — after a
/// restart that lost its last blocks, or when a multi-block branch is retried — is answered from it, not executed
/// twice.</item>
/// <item>A block is held, executed but not committed, until a block builds on it (<see cref="IArkivConfig.HoldHeadBlock"/>):
/// a competing block at the tip executes over the same parent, and whichever one the chain goes on from commits.</item>
/// <item>A store behind Nethermind's chain is brought forward by committing the held block and executing any other
/// missing blocks from Nethermind's own block tree.</item>
/// <item>A reorg below the store's head restores the newest store copy (<see cref="IArkivConfig.CheckpointInterval"/>)
/// on the new branch's ancestry and replays forward from it.</item>
/// </list>
/// Alongside, it keeps what GolemDB cannot: the map from each GolemDB state root to the local account trie's root, and
/// every entity's state after every block that touched it.
/// </remarks>
public sealed class ArkivChain : IDisposable
{
    private const byte ResultPrefix = 0x01;
    private const byte RootPrefix = 0x02;
    private const byte EntityIndexPrefix = 0x03;
    private const byte EntityPrefix = 0x04;
    private static readonly byte[] HeadKey = [0x05, (byte)'H'];
    private static readonly byte[] Tombstone = [0x00];

    private readonly IArkivConfig _config;
    private readonly IDb _db;
    private readonly Lazy<IBlockTree> _blockTree;
    private readonly IEthereumEcdsa _ecdsa;
    private readonly ILogger _logger;
    private readonly string _storeDirectory;
    private readonly string _genesisJson;
    private readonly ClockCache<ValueHash256, Hash256> _roots = new(1 << 16);
    private readonly object _lock = new();
    private ulong _handle;

    public ArkivChain(IArkivConfig config, IInitConfig initConfig, IDb arkivDb, Lazy<IBlockTree> blockTree, IEthereumEcdsa ecdsa, ILogManager logManager)
    {
        _config = config;
        _db = arkivDb;
        _blockTree = blockTree;
        _ecdsa = ecdsa;
        _logger = logManager.GetClassLogger<ArkivChain>();
        // The network's published genesis file is the chain spec too, so it is the default.
        string genesisFile = config.GenesisFile ?? initConfig.ChainSpecPath;
        _genesisJson = File.ReadAllText(ResolveGenesisFile(genesisFile));
        _storeDirectory = config.StoreDirectory ?? DefaultStoreDirectory(initConfig.BaseDbPath);
        ArkivNative.Configure(config.NativeLibrary);
        Open();
    }

    /// <summary>
    /// A relative path is taken from the working directory when it exists there, otherwise from Nethermind's own
    /// directory, as <c>Init.ChainSpecPath</c> is: <c>chainspec/rabdi.json</c> ships beside the Runner.
    /// </summary>
    private static string ResolveGenesisFile(string path) =>
        Path.IsPathRooted(path) || File.Exists(path) ? path : Path.Combine(AppContext.BaseDirectory, path);

    /// <summary>GolemDB's root over the genesis accounts: block 0's state root.</summary>
    public Hash256 GenesisRoot { get; private set; } = Keccak.Zero;

    /// <summary>Block 0's hash, with <see cref="GenesisRoot"/> in its header.</summary>
    public Hash256 GenesisHash { get; private set; } = Keccak.Zero;

    /// <summary>The block the store's head commit holds.</summary>
    public (ulong Number, Hash256 Hash) Head
    {
        get
        {
            byte[]? head = _db.Get(HeadKey);
            return head is null ? (0, GenesisHash) : (BinaryPrimitives.ReadUInt64BigEndian(head), new Hash256(head.AsSpan(8)));
        }
    }

    internal ulong Handle => _handle;

    /// <summary>
    /// <c>arkiv_golemdb</c> beside the plugin's <c>arkiv</c> database. Early builds put the store inside that database's
    /// directory (<c>arkiv/golemdb</c>); a store found there is kept where it is.
    /// </summary>
    public static string DefaultStoreDirectory(string baseDbPath)
    {
        string legacy = Path.Combine(baseDbPath, ArkivModule.DbName, "golemdb");
        return Directory.Exists(legacy) ? legacy : Path.Combine(baseDbPath, "arkiv_golemdb");
    }

    private void Open()
    {
        Directory.CreateDirectory(_storeDirectory);
        byte[] opened = ArkivNative.Open(_genesisJson, _storeDirectory);
        _handle = BinaryPrimitives.ReadUInt64LittleEndian(opened);
        GenesisRoot = new Hash256(opened.AsSpan(8, 32));
        GenesisHash = new Hash256(opened.AsSpan(40, 32));
        byte[] head = ArkivNative.Head(_handle);
        Hash256 tag = new(head.AsSpan(8, 32));
        if (_logger.IsInfo) _logger.Info($"Arkiv store at {_storeDirectory}: genesis {GenesisHash} (root {GenesisRoot}), head commit {BinaryPrimitives.ReadUInt64LittleEndian(head)} block {tag}");
        if (tag == GenesisHash) WriteHead(0, GenesisHash);
    }

    /// <summary>
    /// Executes <paramref name="block"/> on the store's head and, when <paramref name="commit"/> is set and its state
    /// root is the header's, applies it: committed, or held until a block builds on it (<see cref="IArkivConfig.HoldHeadBlock"/>).
    /// Throws <see cref="InvalidBlockException"/> for a block Arkiv refuses.
    /// </summary>
    public ArkivBlockResult Execute(Block block, bool commit)
    {
        if (block.IsGenesis) return ArkivBlockResult.Genesis(GenesisRoot);
        // arkiv-reth refuses any block carrying consensus-layer withdrawals (ArkivPayloadValidator): a withdrawal credits
        // an account outside Arkiv's executor, so GolemDB's balance would never see it. Refused here as there, before
        // anything runs, or the two clients would part ways on such a block.
        if (block.Withdrawals is { Length: > 0 } withdrawals)
        {
            throw new InvalidBlockException(block, $"Arkiv does not support consensus-layer withdrawals; the block carries {withdrawals.Length}");
        }
        ArkivApply apply = !commit ? ArkivApply.DryRun : _config.HoldHeadBlock ? ArkivApply.Hold : ArkivApply.Commit;
        lock (_lock)
        {
            if (TryGetResult(block.Hash!, out ArkivBlockResult? replayed)) return replayed;
            try
            {
                return ExecuteOnHead(block, apply);
            }
            catch (ArkivNativeException e) when (e.Status == ArkivStatus.NotOnHead)
            {
                BringHeadTo(block.Header);
                return ExecuteOnHead(block, apply);
            }
        }
    }

    /// <summary>
    /// Makes the store's head the parent of the block <paramref name="header"/> describes, committing it if it is held:
    /// a block is built over committed state.
    /// </summary>
    public void PrepareBuild(BlockHeader header)
    {
        lock (_lock)
        {
            if (Head.Hash != header.ParentHash) BringHeadTo(header);
        }
    }

    private ArkivBlockResult ExecuteOnHead(Block block, ArkivApply apply)
    {
        byte[] senders = new byte[block.Transactions.Length * Address.Size];
        for (int i = 0; i < block.Transactions.Length; i++)
        {
            Transaction tx = block.Transactions[i];
            Address sender = tx.SenderAddress ??= _ecdsa.RecoverAddress(tx)
                ?? throw new InvalidBlockException(block, $"transaction {i} has no recoverable sender");
            sender.Bytes.CopyTo(senders.AsSpan(i * Address.Size));
        }

        byte[] encoded;
        try
        {
            encoded = ArkivNative.ExecuteBlock(_handle, Rlp.Encode(block), senders, apply);
        }
        catch (ArkivNativeException e) when (e.Status is ArkivStatus.StateRootMismatch)
        {
            throw new InvalidBlockException(block, $"Arkiv state root mismatch: executed to {e.NativeMessage}, the header has {block.StateRoot}");
        }
        catch (ArkivNativeException e) when (e.Status is ArkivStatus.InvalidBlock or ArkivStatus.BadInput)
        {
            throw new InvalidBlockException(block, $"Arkiv refused the block: {e.NativeMessage}");
        }

        ArkivBlockResult result = ArkivBlockResult.Decode(encoded);
        if (apply != ArkivApply.DryRun)
        {
            // Kept for a held block too: Nethermind processing it again is answered from it, and its adoption reads it.
            _db.PutSpan(Key(ResultPrefix, block.Hash!.Bytes), encoded);
            if (apply == ArkivApply.Commit) Committed(block.Header, result);
        }
        return result;
    }

    /// <summary>Records what the store's head moving to <paramref name="header"/>'s block entails beside it.</summary>
    private void Committed(BlockHeader header, ArkivBlockResult result)
    {
        using (IWriteBatch batch = _db.StartWriteBatch())
        {
            // The native side reports each touched entity once per block, so no key repeats within a batch.
            if (_config.EntityHistory) RecordEntities(batch, header.Number, result.Entities);
            batch.Set(HeadKey, HeadValue(header.Number, header.Hash!));
        }
        MaybeCheckpoint(header);
    }

    /// <summary>Commits <paramref name="header"/>'s block if the store holds it over its head; false when it does not.</summary>
    private bool TryAdopt(BlockHeader header)
    {
        if (!TryGetResult(header.Hash!, out ArkivBlockResult? result)) return false;
        try
        {
            ArkivNative.Adopt(_handle, header.Hash!.Bytes);
        }
        catch (ArkivNativeException e) when (e.Status == ArkivStatus.NotOnHead)
        {
            return false;
        }
        Committed(header, result);
        return true;
    }

    /// <summary>
    /// The block the store's head commit holds, from the store itself: after a restart it can be behind the recorded
    /// head, which counts blocks that changed nothing and are known to the store only in memory.
    /// </summary>
    private void ReadHeadFromStore()
    {
        Hash256 tag = new(ArkivNative.Head(_handle).AsSpan(8, 32));
        if (tag == Head.Hash) return;
        if (_blockTree.Value.FindHeader(tag, BlockTreeLookupOptions.None) is { } header) WriteHead(header.Number, tag);
    }

    /// <summary>Makes the store's head <paramref name="target"/>'s parent: forward along its ancestry, or back through a copy.</summary>
    private void BringHeadTo(BlockHeader target)
    {
        ReadHeadFromStore();
        IBlockTree tree = _blockTree.Value;
        (ulong headNumber, Hash256 headHash) = Head;
        List<BlockHeader> ancestry = [];
        BlockHeader? cursor = tree.FindHeader(target.ParentHash!, BlockTreeLookupOptions.None)
            ?? throw new InvalidOperationException($"Arkiv: block {target.ToString(BlockHeader.Format.Short)} has no known parent");
        while (cursor.Number > headNumber)
        {
            ancestry.Add(cursor);
            cursor = tree.FindHeader(cursor.ParentHash!, BlockTreeLookupOptions.None)
                ?? throw new InvalidOperationException($"Arkiv: missing ancestor {cursor.ParentHash}");
        }

        if (cursor.Hash != headHash)
        {
            // A fork below the store's head: GolemDB cannot rewind, so restart from a copy taken on this ancestry.
            if (_logger.IsWarn) _logger.Warn($"Arkiv: block {target.ToString(BlockHeader.Format.Short)} forks below the store's head {headNumber} ({headHash}); restoring a store copy");
            BlockHeader from = RestoreCheckpointOnAncestry(cursor, ancestry);
            ancestry.Clear();
            cursor = tree.FindHeader(target.ParentHash!, BlockTreeLookupOptions.None)!;
            while (cursor.Number > from.Number)
            {
                ancestry.Add(cursor);
                cursor = tree.FindHeader(cursor.ParentHash!, BlockTreeLookupOptions.None)!;
            }
        }

        // The block right above the head is usually held: then it only commits. Anything else executes from the tree.
        int executed = 0;
        for (int i = ancestry.Count - 1; i >= 0; i--)
        {
            if (TryAdopt(ancestry[i])) continue;
            Block block = tree.FindBlock(ancestry[i].Hash!, BlockTreeLookupOptions.None)
                ?? throw new InvalidOperationException($"Arkiv: block {ancestry[i].ToString(BlockHeader.Format.Short)} has no body to replay");
            ExecuteOnHead(block, ArkivApply.Commit);
            executed++;
        }
        if (executed > 0 && _logger.IsWarn)
        {
            _logger.Warn($"Arkiv: the store's head {headNumber} was {ancestry.Count} blocks behind block {target.ToString(BlockHeader.Format.Short)}'s parent; executed {executed} of them from the block tree");
        }
    }

    public bool TryGetResult(Hash256 blockHash, out ArkivBlockResult result)
    {
        if (TryGetResultBytes(blockHash, out byte[]? bytes))
        {
            result = ArkivBlockResult.Decode(bytes);
            return true;
        }
        result = null!;
        return false;
    }

    private bool TryGetResultBytes(Hash256 blockHash, out byte[] bytes)
    {
        bytes = _db.Get(Key(ResultPrefix, blockHash.Bytes))!;
        return bytes is not null;
    }

    // ---- block building ------------------------------------------------------------------------------------------

    /// <summary>The keys the purge of block <paramref name="number"/> names, chosen at the store's head (its parent) as arkiv-reth chooses them.</summary>
    public IReadOnlyList<Hash256> PurgeKeys(ulong number)
    {
        byte[] bytes;
        lock (_lock) bytes = ArkivNative.PurgeKeys(_handle, number);
        Hash256[] keys = new Hash256[bytes.Length / 32];
        for (int i = 0; i < keys.Length; i++) keys[i] = new Hash256(bytes.AsSpan(32 * i, 32));
        return keys;
    }

    /// <summary>Opens a native build of the block <paramref name="header"/> describes; its parent must be the store's head.</summary>
    public ArkivBuild BeginBuild(BlockHeader header)
    {
        lock (_lock) return new ArkivBuild(ArkivNative.BuildBegin(_handle, Rlp.Encode(header)));
    }

    // ---- state root map -------------------------------------------------------------------------------------------

    /// <summary>Records that the block whose header carries GolemDB root <paramref name="arkivRoot"/> has local account trie root <paramref name="trieRoot"/>.</summary>
    public void MapRoot(Hash256 arkivRoot, Hash256 trieRoot)
    {
        _roots.Set(arkivRoot, trieRoot);
        _db.PutSpan(Key(RootPrefix, arkivRoot.Bytes), trieRoot.Bytes);
    }

    /// <summary>The local account trie root for a header's GolemDB root, if this node processed that block.</summary>
    public Hash256? TrieRootOf(Hash256? arkivRoot)
    {
        if (arkivRoot is null) return null;
        if (_roots.TryGet(arkivRoot, out Hash256? cached)) return cached;
        byte[]? stored = _db.Get(Key(RootPrefix, arkivRoot.Bytes));
        if (stored is null) return null;
        Hash256 root = new(stored);
        _roots.Set(arkivRoot, root);
        return root;
    }

    // ---- entity history -------------------------------------------------------------------------------------------

    private void RecordEntities(IWriteBatch batch, ulong number, ArkivEntitySnapshot[] entities)
    {
        Span<byte> numberBytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(numberBytes, number);
        foreach (ArkivEntitySnapshot entity in entities)
        {
            byte[] indexKey = Key(EntityIndexPrefix, entity.Key.Bytes);
            byte[] index = _db.Get(indexKey) ?? [];
            if (index.Length < 8 || !index.AsSpan(index.Length - 8).SequenceEqual(numberBytes))
            {
                byte[] grown = new byte[index.Length + 8];
                index.CopyTo(grown, 0);
                numberBytes.CopyTo(grown.AsSpan(index.Length));
                batch.Set(indexKey, grown);
            }
            batch.Set(EntityKey(entity.Key, number), entity.Json ?? Tombstone);
        }
    }

    /// <summary>
    /// The entity's <c>arkiv_getEntity</c> JSON as of the end of block <paramref name="number"/>, from the archive this
    /// node keeps. <c>Found</c> is false when no block up to <paramref name="number"/> touched it.
    /// </summary>
    public (bool Found, JsonElement? Entity) EntityAt(Hash256 key, ulong number)
    {
        byte[]? index = _db.Get(Key(EntityIndexPrefix, key.Bytes));
        if (index is null) return (false, null);
        int count = index.Length / 8;
        int lo = 0, hi = count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            ulong at = BinaryPrimitives.ReadUInt64BigEndian(index.AsSpan(mid * 8));
            if (at <= number) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        if (found < 0) return (false, null);
        ulong touched = BinaryPrimitives.ReadUInt64BigEndian(index.AsSpan(found * 8));
        byte[]? json = _db.Get(EntityKey(key, touched));
        if (json is null || json.AsSpan().SequenceEqual(Tombstone)) return (true, null);
        return (true, JsonDocument.Parse(json).RootElement.Clone());
    }

    // ---- reads ----------------------------------------------------------------------------------------------------

    /// <summary>An <c>arkiv_*</c> method answered natively at the store's head; the JSON holds <c>result</c> or <c>error</c>.</summary>
    /// <remarks>
    /// Reads and calls take the execution lock: a commit invalidates every view over the head it replaces, so a read
    /// racing one fails with <c>HandleInvalid</c> (and a call's speculative branch with it), and the head's block
    /// number must be the one the read ran at.
    /// </remarks>
    public JsonElement Rpc(string method, string parametersJson)
    {
        byte[] response;
        lock (_lock) response = ArkivNative.Rpc(_handle, method, parametersJson, Head.Number);
        return JsonDocument.Parse(response).RootElement.Clone();
    }

    /// <summary>A non-committing call at the store's head.</summary>
    public JsonElement Call(object request)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(request);
        byte[] response;
        lock (_lock) response = ArkivNative.Call(_handle, payload);
        return JsonDocument.Parse(response).RootElement.Clone();
    }

    /// <summary>An account at the store's head, as GolemDB holds it.</summary>
    public (bool Exists, UInt256 Balance, ulong Nonce) Account(Address address)
    {
        byte[] bytes;
        lock (_lock) bytes = ArkivNative.Account(_handle, address.Bytes);
        return (bytes[0] != 0, new UInt256(bytes.AsSpan(1, 32), isBigEndian: true), BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(33)));
    }

    // ---- store copies ---------------------------------------------------------------------------------------------

    private string CheckpointRoot => _storeDirectory + ".checkpoints";

    private void MaybeCheckpoint(BlockHeader header)
    {
        ulong interval = _config.CheckpointInterval;
        if (interval == 0 || header.Number % interval != 0) return;
        try
        {
            Checkpoint(header);
        }
        catch (Exception e)
        {
            // A copy is a recovery aid; the block is committed either way, and a failed copy must not fail it.
            if (_logger.IsError) _logger.Error($"Arkiv: store copy at block {header.ToString(BlockHeader.Format.Short)} failed", e);
        }
    }

    private void Checkpoint(BlockHeader header)
    {
        string target = Path.Combine(CheckpointRoot, $"{header.Number:D12}-{header.Hash}");
        string staging = target + ".partial";
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        // Under the execution lock no commit runs, so the store's files are those of this head commit.
        CopyDirectory(_storeDirectory, staging);
        Directory.Move(staging, target);
        // Two copies: the newest, and the one before it should a reorg reach behind the newest.
        foreach (string old in Directory.GetDirectories(CheckpointRoot).Where(d => !d.EndsWith(".partial", StringComparison.Ordinal)).OrderByDescending(d => d).Skip(2))
        {
            Directory.Delete(old, recursive: true);
        }
        if (_logger.IsInfo) _logger.Info($"Arkiv: store copy at block {header.ToString(BlockHeader.Format.Short)}");
    }

    private BlockHeader RestoreCheckpointOnAncestry(BlockHeader forkCursor, List<BlockHeader> ancestry)
    {
        IBlockTree tree = _blockTree.Value;
        if (Directory.Exists(CheckpointRoot))
        {
            foreach (string copy in Directory.GetDirectories(CheckpointRoot).Where(d => !d.EndsWith(".partial", StringComparison.Ordinal)).OrderByDescending(d => d))
            {
                string[] parts = Path.GetFileName(copy).Split('-', 2);
                ulong number = ulong.Parse(parts[0]);
                Hash256 hash = new(parts[1]);
                BlockHeader? onAncestry = number <= forkCursor.Number ? FindAncestor(tree, forkCursor, number) : ancestry.Find(h => h.Number == number);
                if (onAncestry?.Hash != hash) continue;

                ArkivNative.arkiv_close(_handle);
                _handle = 0;
                Directory.Delete(_storeDirectory, recursive: true);
                CopyDirectory(copy, _storeDirectory);
                Open();
                WriteHead(number, hash);
                return onAncestry;
            }
        }
        throw new InvalidOperationException(
            "Arkiv: a reorg reaches below the GolemDB store's head and no store copy lies on the new branch. GolemDB cannot rewind; " +
            $"resync the Arkiv store or set {nameof(IArkivConfig)}.{nameof(IArkivConfig.CheckpointInterval)}.");
    }

    private static BlockHeader? FindAncestor(IBlockTree tree, BlockHeader from, ulong number)
    {
        BlockHeader? cursor = from;
        while (cursor is not null && cursor.Number > number) cursor = tree.FindHeader(cursor.ParentHash!, BlockTreeLookupOptions.None);
        return cursor;
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.GetFiles(from))
        {
            // Lock files (MDBX's mdbx.lck, the immutable-data .lock) belong to the running store and hold no data.
            string name = Path.GetFileName(file);
            if (name is ".lock" or "mdbx.lck") continue;
            File.Copy(file, Path.Combine(to, name));
        }
        foreach (string dir in Directory.GetDirectories(from)) CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
    }

    // ---- state sync -----------------------------------------------------------------------------------------------

    private string ServedRoot => _storeDirectory + ".served";
    private readonly object _servedLock = new();
    private ArkivStoreCopy? _served;
    // The copy before the newest, kept readable for peers still fetching it.
    private ArkivStoreCopy? _servedBefore;
    private bool _servedLoaded;
    private Task _copying = Task.CompletedTask;

    /// <summary>
    /// The copy of the store this node serves to peers that state-sync, or null when it has none at
    /// <paramref name="minNumber"/> or later yet.
    /// </summary>
    /// <remarks>
    /// A copy older than an asker can use is replaced by one made at the head in the background, and the asker asks again.
    /// A copy is several gigabytes, so a new one is made at most once every <see cref="IArkivConfig.ServedCopyAge"/>
    /// blocks: peers asking for ever newer ones cannot keep the node copying.
    /// </remarks>
    public ArkivStoreCopy? ServedCopy(ulong minNumber)
    {
        if (!_config.ServeState) return null;
        lock (_servedLock)
        {
            if (!_servedLoaded)
            {
                LoadServedCopies();
                _servedLoaded = true;
            }
            ulong head = Head.Number;
            bool tooOld = _served is null || _served.Number < minNumber;
            bool mayCopy = _served is null || head >= _served.Number + _config.ServedCopyAge;
            if (tooOld && mayCopy && head > 0 && _copying.IsCompleted) _copying = Task.Run(CopyForServing);
            return _served is not null && _served.Number >= minNumber ? _served : null;
        }
    }

    private void CopyForServing()
    {
        try
        {
            string target;
            lock (_lock)
            {
                // Under the execution lock no commit runs, so the store's files are those of this head commit.
                (ulong number, Hash256 hash) = Head;
                target = Path.Combine(ServedRoot, $"{number:D12}-{hash}");
                if (!Directory.Exists(target))
                {
                    string staging = target + ".partial";
                    if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
                    CopyDirectory(_storeDirectory, staging);
                    Directory.Move(staging, target);
                }
            }
            ArkivStoreCopy copy = DescribeCopy(target);
            lock (_servedLock)
            {
                if (_served?.Hash != copy.Hash) _servedBefore = _served;
                _served = copy;
                foreach (string old in Directory.GetDirectories(ServedRoot))
                {
                    if (old != _served.Directory && old != _servedBefore?.Directory) Directory.Delete(old, recursive: true);
                }
            }
            if (_logger.IsInfo) _logger.Info($"Arkiv: serving a store copy at block {copy.Number} ({copy.Hash}) to peers that state-sync");
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error("Arkiv: making a store copy to serve failed", e);
        }
    }

    private void LoadServedCopies()
    {
        if (!Directory.Exists(ServedRoot)) return;
        string[] copies = Directory.GetDirectories(ServedRoot).Where(d => !d.EndsWith(".partial", StringComparison.Ordinal)).OrderByDescending(d => d).ToArray();
        foreach (string partial in Directory.GetDirectories(ServedRoot).Where(d => d.EndsWith(".partial", StringComparison.Ordinal))) Directory.Delete(partial, recursive: true);
        if (copies.Length > 0) _served = DescribeCopy(copies[0]);
        if (copies.Length > 1) _servedBefore = DescribeCopy(copies[1]);
    }

    private static ArkivStoreCopy DescribeCopy(string directory)
    {
        string[] parts = Path.GetFileName(directory).Split('-', 2);
        List<(string, long)> files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .Select(f => (Path.GetRelativePath(directory, f).Replace(Path.DirectorySeparatorChar, '/'), new FileInfo(f).Length))
            .OrderBy(f => f.Item1, StringComparer.Ordinal)
            .ToList();
        return new ArkivStoreCopy(ulong.Parse(parts[0]), new Hash256(parts[1]), directory, files);
    }

    /// <summary>
    /// Up to <paramref name="length"/> bytes at <paramref name="offset"/> of file <paramref name="file"/> of the served copy
    /// at block <paramref name="hash"/>; null when this node no longer serves that copy or the range is outside the file.
    /// </summary>
    public byte[]? ReadServedChunk(Hash256 hash, int file, long offset, int length)
    {
        ArkivStoreCopy? copy;
        lock (_servedLock) copy = _served?.Hash == hash ? _served : _servedBefore?.Hash == hash ? _servedBefore : null;
        if (copy is null || (uint)file >= (uint)copy.Files.Count) return null;
        (string path, long size) = copy.Files[file];
        if (offset < 0 || offset > size) return null;
        int count = (int)Math.Min(length, size - offset);
        byte[] data = new byte[count];
        try
        {
            using FileStream stream = new(Path.Combine(copy.Directory, path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(offset, SeekOrigin.Begin);
            stream.ReadExactly(data);
        }
        catch (IOException)
        {
            // Replaced between the lookup and the read.
            return null;
        }
        return data;
    }

    /// <summary>
    /// Makes the store in <paramref name="directory"/>, fetched from a peer, this node's store, provided its head commit is
    /// <paramref name="header"/>'s block — tagged with its hash and with the root its header carries — on this chain.
    /// Otherwise the node's own store is put back and the call throws. Returns every account the store holds, with its
    /// balance and nonce, for the local account trie.
    /// </summary>
    public IReadOnlyList<(Address Address, UInt256 Balance, ulong Nonce)> InstallStore(string directory, BlockHeader header)
    {
        // Listed while no environment is open on the directory.
        byte[] listed = ArkivNative.StoreAccounts(directory);
        lock (_lock)
        {
            string replaced = _storeDirectory + ".replaced";
            if (Directory.Exists(replaced)) Directory.Delete(replaced, recursive: true);
            ArkivNative.arkiv_close(_handle);
            _handle = 0;
            Directory.Move(_storeDirectory, replaced);
            Directory.Move(directory, _storeDirectory);
            try
            {
                // Open refuses a store of another chain (no commit tagged with this genesis).
                Open();
                byte[] head = ArkivNative.Head(_handle);
                Hash256 tag = new(head.AsSpan(8, 32));
                Hash256 root = new(head.AsSpan(40, 32));
                if (tag != header.Hash || root != header.StateRoot)
                {
                    throw new InvalidDataException($"the store's head is block {tag} with root {root}, not block {header.ToString(BlockHeader.Format.Short)} with root {header.StateRoot}");
                }
            }
            catch
            {
                if (_handle != 0) ArkivNative.arkiv_close(_handle);
                _handle = 0;
                Directory.Delete(_storeDirectory, recursive: true);
                Directory.Move(replaced, _storeDirectory);
                Open();
                throw;
            }
            Directory.Delete(replaced, recursive: true);
            WriteHead(header.Number, header.Hash!);

            List<(Address, UInt256, ulong)> accounts = [with(listed.Length / Address.Size)];
            for (int i = 0; i < listed.Length; i += Address.Size)
            {
                Address address = new(listed.AsSpan(i, Address.Size));
                byte[] account = ArkivNative.Account(_handle, address.Bytes);
                if (account[0] == 0) continue;
                accounts.Add((address, new UInt256(account.AsSpan(1, 32), isBigEndian: true), BinaryPrimitives.ReadUInt64LittleEndian(account.AsSpan(33))));
            }
            return accounts;
        }
    }

    // ---- keys -----------------------------------------------------------------------------------------------------

    private void WriteHead(ulong number, Hash256 hash) => _db.Set(HeadKey, HeadValue(number, hash));

    private static byte[] HeadValue(ulong number, Hash256 hash)
    {
        byte[] value = new byte[40];
        BinaryPrimitives.WriteUInt64BigEndian(value, number);
        hash.Bytes.CopyTo(value.AsSpan(8));
        return value;
    }

    private static byte[] Key(byte prefix, ReadOnlySpan<byte> id)
    {
        byte[] key = new byte[1 + id.Length];
        key[0] = prefix;
        id.CopyTo(key.AsSpan(1));
        return key;
    }

    private static byte[] EntityKey(Hash256 entity, ulong number)
    {
        byte[] key = new byte[1 + 32 + 8];
        key[0] = EntityPrefix;
        entity.Bytes.CopyTo(key.AsSpan(1));
        BinaryPrimitives.WriteUInt64BigEndian(key.AsSpan(33), number);
        return key;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_handle != 0) ArkivNative.arkiv_close(_handle);
            _handle = 0;
        }
    }

    /// <summary>The directory the node's GolemDB store lives in.</summary>
    public string StoreDirectory => _storeDirectory;

    public override string ToString() => $"Arkiv store {_storeDirectory} at block {Head.Number}";
}

/// <summary>A copy of the GolemDB store at block <see cref="Number"/> (<see cref="Hash"/>), served to peers that state-sync.</summary>
public sealed record ArkivStoreCopy(ulong Number, Hash256 Hash, string Directory, IReadOnlyList<(string Path, long Size)> Files);
