// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Db.LogIndex;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.TraceStore.Ots;

internal sealed class TracePositionSidecar(
    IColumnsDb<LogIndexColumns> db,
    IDb traces,
    ITraceSerializer<ParityLikeTxTrace> serializer,
    IBlockFinder blocks,
    ITraceStoreConfig config,
    ILogger logger)
{
    internal const string Switch = "Nethermind.Experimental.OtsPositionSidecarP1";
    internal static ReadOnlySpan<byte> Prefix => "ots-pos-p1/"u8;
    internal static readonly byte[] SchemaKey = Key(0);
    internal static readonly byte[] ProgressKey = Key(1);
    private readonly IDb _meta = db.GetColumnDb(LogIndexColumns.Meta);
    private readonly JsonSerializerOptions _options = EthereumJsonSerializer.JsonOptions;
    private volatile bool _ready;
    private bool _loaded;
    private bool _disabled;
    private ulong _next;
    private int _building;
    private string? _lastPause;
    private bool _loggedFirstMatch;

    private bool Eligible => !_disabled && serializer.GetType() == typeof(ParityLikeTraceSerializer)
        && ReferenceEquals(_options, EthereumJsonSerializer.JsonOptions);

    // Caller holds the index lifecycle lock; all leases end before returning to RPC receipt processing.
    internal SearchResult<bool[]>? TryMatch(Block block, Address address, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!_ready || !Eligible) return null;
        if ((config.TraceTypes & ParityTraceTypes.Trace) == 0)
            return new("OTS requires TraceStore.TraceTypes to include Trace.", ErrorCodes.ResourceUnavailable);
        byte[] key = Key(2, block.Hash!);
        Span<byte> record = _meta.GetSpan(key);
        try
        {
            if (record.IsNull()) return null;
            Span<byte> blob = traces.GetSpan(block.Hash!);
            try
            {
                bool[]? matches = TracePositionRecord.Match(record, block, address, blob, ct);
                if (!Eligible || matches is null) return null;
                if (!_loggedFirstMatch && logger.IsInfo)
                {
                    _loggedFirstMatch = true;
                    logger.Info($"Position sidecar first proof match: {block.Number} ({block.Hash}).");
                }
                return new(matches);
            }
            catch (InvalidDataException)
            {
                return new($"Incomplete or inconsistent transaction traces for block {block.Number}.", ErrorCodes.ResourceUnavailable);
            }
            finally { traces.DangerousReleaseMemory(blob); }
        }
        finally { _meta.DangerousReleaseMemory(record); }
    }

    internal bool BuildNext(TraceAddressIndex.Checkpoint coverage, CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _building, 1, 0) != 0)
            throw new InvalidOperationException("The position sidecar requires a single writer.");
        try { return BuildNextCore(coverage, ct); }
        finally { Volatile.Write(ref _building, 0); }
    }

    private bool BuildNextCore(TraceAddressIndex.Checkpoint coverage, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Eligible || blocks.Head is not { } head) return false;
        if (!_loaded) Load(coverage, ct);
        if (!_ready || _next > (ulong)coverage.To || head.Number < (ulong)config.AddressIndexLag
            || _next > head.Number - (ulong)config.AddressIndexLag) return false;
        Block? block = blocks.FindBlock(_next);
        if (block is null) return Pause($"Block {_next} unavailable");
        if (!Canonical(block, coverage)) return Pause("Canonical sidecar coverage changed");
        byte[]? record = null;
        if (block.Transactions.Length != 0 && block.Transactions.Length <= TracePositionRecord.MaxTransactions)
        {
            Span<byte> blob = traces.GetSpan(block.Hash!);
            try
            {
                if (blob.IsEmpty) return Pause($"Stored traces for block {_next} unavailable");
                if (blob.Length <= TracePositionRecord.MaxBlobBytes && FitsInflatedLimit(blob, ct))
                {
                    List<ParityLikeTxTrace>? decoded = serializer.Deserialize(blob);
                    if (decoded is null) return Pause($"Stored traces for block {_next} unavailable");
                    record = TracePositionRecord.Create(block, decoded, blob, ct);
                }
            }
            catch (Exception error) when (error is InvalidDataException or JsonException or FormatException)
            {
                return Pause($"Stored traces for block {_next} invalid: {error.Message}");
            }
            finally { traces.DangerousReleaseMemory(blob); }
        }
        byte[] key = Key(2, block.Hash!);
        byte[]? existing = _meta.Get(key);
        if (existing is not null && (record is null || !existing.AsSpan().SequenceEqual(record)))
            return Pause($"Existing position proof for block {_next} differs; no automatic overwrite");
        ct.ThrowIfCancellationRequested();
        if (!Eligible || !Canonical(block, coverage)) return false;
        byte[] progress = Progress(block.Number + 1, block.Hash!);
        Publish(key, existing is null ? record : null, progress);
        _next = block.Number + 1;
        _lastPause = null;
        if (_next > (ulong)coverage.To && logger.IsInfo)
            logger.Info($"Position sidecar caught up through {block.Number} ({block.Hash}).");
        return true;
    }

    private void Load(TraceAddressIndex.Checkpoint coverage, CancellationToken ct)
    {
        byte[] expected = Schema();
        byte[]? schema = _meta.Get(SchemaKey);
        if (schema is null)
        {
            foreach (byte[] key in _meta.GetAllKeys())
            {
                ct.ThrowIfCancellationRequested();
                if (key.AsSpan().StartsWith(Prefix))
                {
                    Disable("orphan position namespace");
                    return;
                }
            }
            Publish(SchemaKey, expected, Progress((ulong)coverage.From, Hash256.Zero));
        }
        else if (!schema.AsSpan().SequenceEqual(expected))
        {
            Disable("incompatible position namespace");
            return;
        }
        _next = (ulong)coverage.From;
        byte[]? progress = _meta.Get(ProgressKey);
        if (progress is { Length: 40 })
        {
            ulong next = BinaryPrimitives.ReadUInt64LittleEndian(progress);
            if (next >= _next && next <= (ulong)coverage.To + 1
                && (next == _next || blocks.FindBlockHash(next - 1) == new Hash256(progress.AsSpan(8)))) _next = next;
        }
        _loaded = true;
        _ready = true;
        if (logger.IsInfo)
            logger.Info($"Position sidecar ready: next={_next}, eligible={Eligible}, schema={Convert.ToHexString(expected)}.");
    }

    private void Publish(byte[] key, byte[]? value, byte[] progress)
    {
        using (IColumnsWriteBatch<LogIndexColumns> batch = db.StartWriteBatch())
        {
            try
            {
                IWriteBatch meta = batch.GetColumnBatch(LogIndexColumns.Meta);
                if (value is not null) meta.Set(key, value);
                meta.Set(ProgressKey, progress);
            }
            catch
            {
                batch.Clear();
                throw;
            }
        }
        db.SyncWal();
    }

    private byte[] Schema()
    {
        byte[] value = new byte[84];
        "OTP1"u8.CopyTo(value);
        BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(4), TracePositionRecord.Version);
        BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(8), TracePositionRecord.Version);
        blocks.GenesisHash.Bytes.CopyTo(value.AsSpan(12, 32));
        BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(44), config.AddressIndexFromBlock);
        BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(48), config.MaxDepth);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Type[] contracts = [typeof(ParityLikeTraceSerializer), typeof(ParityLikeTxTrace), typeof(Address),
            typeof(EthereumJsonSerializer), typeof(SearchResult<>), typeof(JsonSerializer), typeof(object), typeof(SHA256)];
        foreach (Type type in contracts) hash.AppendData(type.Assembly.ManifestModule.ModuleVersionId.ToByteArray());
        byte[] runtime = Encoding.UTF8.GetBytes(Environment.Version.ToString());
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(number, runtime.Length);
        hash.AppendData(number);
        hash.AppendData(runtime);
        BinaryPrimitives.WriteInt32LittleEndian(number, TracePositionRecord.Version);
        hash.AppendData(number);
        hash.GetHashAndReset(value.AsSpan(52));
        return value;
    }

    private bool Canonical(Block block, TraceAddressIndex.Checkpoint coverage) => blocks.GenesisHash == coverage.Genesis
        && blocks.FindBlockHash((ulong)coverage.To) == coverage.Hash
        && blocks.FindBlockHash(block.Number) == block.Hash && blocks.IsMainChain(block.Header);

    private static bool FitsInflatedLimit(ReadOnlySpan<byte> blob, CancellationToken ct)
    {
        using MemoryStream input = new(blob.ToArray(), writable: false);
        using GZipStream gzip = new(input, CompressionMode.Decompress);
        Span<byte> buffer = stackalloc byte[4096];
        int total = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int count = gzip.Read(buffer);
            if (count == 0) return true;
            total += count;
            if (total > TracePositionRecord.MaxBlobBytes) return false;
        }
    }

    internal static byte[] Key(byte kind, Hash256? hash = null)
    {
        byte[] key = new byte[Prefix.Length + 1 + (hash is null ? 0 : 32)];
        Prefix.CopyTo(key);
        key[Prefix.Length] = kind;
        if (hash is not null) hash.Bytes.CopyTo(key.AsSpan(Prefix.Length + 1));
        return key;
    }

    private static byte[] Progress(ulong next, Hash256 hash)
    {
        byte[] value = new byte[40];
        BinaryPrimitives.WriteUInt64LittleEndian(value, next);
        hash.Bytes.CopyTo(value.AsSpan(8));
        return value;
    }

    private bool Pause(string reason)
    {
        if (_lastPause != reason && logger.IsWarn) logger.Warn($"Position sidecar paused: {reason}.");
        _lastPause = reason;
        return false;
    }

    private void Disable(string reason)
    {
        _disabled = true;
        _loaded = true;
        if (logger.IsWarn) logger.Warn($"Position sidecar disabled: {reason}; OTS uses stored traces.");
    }
}
