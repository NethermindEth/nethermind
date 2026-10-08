// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Security.Cryptography;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

namespace Nethermind.LightClient.Consensus;

/// <summary>Persists the updates needed to reconstruct a finalized light-client store from its trusted checkpoint.</summary>
/// <remarks>
/// The committee chain and latest finalized head are written atomically to separate files. On restart every
/// retained update is verified again; the saved files never become a new trust anchor. One synchronization owner
/// calls the mutating methods after successful <see cref="LightClientStore.Process(LightClientUpdate, ulong)"/>.
/// </remarks>
internal sealed class VerifiedConsensusJournal
{
    private const int MaxRecordBytes = 256 * 1024;
    private const int MaxChainBytes = 128 * 1024 * 1024;
    private const int MaxUpdates = 4096;
    private const int DigestBytes = 32;
    private static ReadOnlySpan<byte> Magic => "NLCJ"u8;

    private readonly BeaconChainSpec _spec;
    private readonly Hash256 _checkpoint;
    private readonly byte _network;
    private readonly string _chainPath;
    private readonly string _headPath;
    private readonly List<(byte Kind, byte[] Data)> _updates = [];
    private byte[]? _bootstrap;
    private byte[]? _chainDigest;
    private (byte Kind, byte[] Data)? _latestHead;
    private ulong _period;
    private ulong _slot;
    private bool _nextKnown;
    private int _previousMaxParticipants;
    private int _currentMaxParticipants;

    internal VerifiedConsensusJournal(string directory, BeaconChainSpec spec, string network, Hash256 trustedCheckpoint)
    {
        _spec = spec;
        _checkpoint = trustedCheckpoint;
        _network = network switch
        {
            "mainnet" => 1,
            "hoodi" => 2,
            "sepolia" => 3,
            _ => throw new ArgumentException("Unsupported light-client network.", nameof(network)),
        };
        string prefix = $"{network}-{Convert.ToHexString(trustedCheckpoint.Bytes)}";
        _chainPath = Path.Combine(directory, prefix + ".chain");
        _headPath = Path.Combine(directory, prefix + ".head");
    }

    /// <summary>Replays the saved bootstrap and updates, or returns null when no journal exists.</summary>
    /// <exception cref="InvalidDataException">The saved committee chain cannot be authenticated.</exception>
    internal async Task<LightClientStore?> LoadAsync(ulong currentSlot, CancellationToken cancellationToken)
    {
        if (!File.Exists(_chainPath)) return null;
        byte[] chain = await ReadBoundedAsync(_chainPath, MaxChainBytes, cancellationToken);
        byte[] payload = VerifyEnvelope(chain, 0).ToArray();
        byte[] chainDigest = chain.AsSpan(chain.Length - DigestBytes).ToArray();
        int offset = 39;
        byte[] bootstrapBytes = ReadRecord(payload, ref offset);
        LightClientBootstrap bootstrap = LightClientWireCodec.DecodeBootstrap(bootstrapBytes, _spec);
        ulong bootstrapSlot = bootstrap.Header?.Beacon?.Slot ?? throw new InvalidDataException("Saved bootstrap has no slot.");
        if (bootstrapSlot > currentSlot) throw new InvalidDataException("Saved checkpoint is in the future.");
        LightClientStore store = new(_spec, _checkpoint, bootstrap, currentSlot);
        int count = ReadInt32(payload, ref offset);
        if (count is < 0 or > MaxUpdates) throw new InvalidDataException("Saved committee chain has too many updates.");
        List<(byte Kind, byte[] Data)> updates = new(count);
        for (int i = 0; i < count; i++)
        {
            byte kind = ReadByte(payload, ref offset);
            byte[] encoded = ReadRecord(payload, ref offset);
            ProcessRecord(store, kind, encoded, currentSlot);
            updates.Add((kind, encoded));
        }
        if (offset != payload.Length) throw new InvalidDataException("Saved committee chain has trailing data.");

        (byte Kind, byte[] Data)? latestHead = null;
        if (File.Exists(_headPath))
        {
            try
            {
                byte[] head = await ReadBoundedAsync(_headPath, MaxRecordBytes + 108, cancellationToken);
                byte[] headPayload = VerifyEnvelope(head, 1).ToArray();
                if (!headPayload.AsSpan(39, DigestBytes).SequenceEqual(chainDigest))
                    throw new InvalidDataException("Saved head refers to an older committee chain.");
                int headOffset = 39 + DigestBytes;
                byte kind = ReadByte(headPayload, ref headOffset);
                byte[] encoded = ReadRecord(headPayload, ref headOffset);
                if (headOffset != headPayload.Length) throw new InvalidDataException("Saved head has trailing data.");
                ProcessRecord(store, kind, encoded, currentSlot);
                latestHead = (kind, encoded);
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or OverflowException or ArgumentException)
            {
                // The committee chain remains authenticated, so an invalid optional head is discarded.
            }
        }

        _bootstrap = bootstrapBytes;
        _updates.Clear();
        _updates.AddRange(updates);
        _chainDigest = chainDigest;
        _latestHead = latestHead;
        RecordState(store);
        return store;
    }

    /// <summary>Atomically saves an authenticated bootstrap as the start of a new journal.</summary>
    internal async Task InitializeAsync(LightClientBootstrap bootstrap, LightClientStore store, CancellationToken cancellationToken)
    {
        byte[] encoded = LightClientWireCodec.EncodeBootstrap(bootstrap, _spec);
        byte[] chain = BuildChain(encoded, []);
        await WriteAtomicAsync(_chainPath, chain, cancellationToken);
        _bootstrap = encoded;
        _updates.Clear();
        _chainDigest = chain.AsSpan(chain.Length - DigestBytes).ToArray();
        _latestHead = null;
        RecordState(store);
    }

    /// <summary>Saves a verified full update before its resulting state is published.</summary>
    internal async Task AppendAsync(LightClientUpdate update, LightClientStore store, CancellationToken cancellationToken)
    {
        if (RequiresChainRecord(store))
        {
            await AppendChainAsync(1, LightClientWireCodec.EncodeUpdate(update, _spec), cancellationToken);
        }
        else if (store.FinalizedHeader.Beacon!.Slot > _slot)
        {
            byte[] encoded = LightClientWireCodec.EncodeUpdate(update, _spec);
            await WriteAtomicAsync(_headPath, BuildHead(1, encoded), cancellationToken);
            _latestHead = (1, encoded);
        }
        RecordState(store);
    }

    /// <summary>Saves a verified finality update before its resulting state is published.</summary>
    internal async Task AppendAsync(LightClientFinalityUpdate update, LightClientStore store, CancellationToken cancellationToken)
    {
        if (RequiresChainRecord(store))
        {
            await AppendChainAsync(2, LightClientWireCodec.EncodeFinality(update, _spec), cancellationToken);
        }
        else if (store.FinalizedHeader.Beacon!.Slot > _slot)
        {
            byte[] encoded = LightClientWireCodec.EncodeFinality(update, _spec);
            await WriteAtomicAsync(_headPath, BuildHead(2, encoded), cancellationToken);
            _latestHead = (2, encoded);
        }
        RecordState(store);
    }

    /// <summary>Saves an authenticated optimistic update when it raises the participation safety threshold.</summary>
    internal async Task AppendAsync(LightClientOptimisticUpdate update, LightClientStore store, CancellationToken cancellationToken)
    {
        if (RequiresChainRecord(store))
            await AppendChainAsync(4, LightClientWireCodec.EncodeOptimistic(update, _spec), cancellationToken);
        RecordState(store);
    }

    /// <summary>Persists the authenticated candidate and timeout that advanced the sync committee without finality.</summary>
    internal async Task AppendForcedAsync(LightClientUpdate bestUpdate, ulong forceSlot, LightClientStore store,
        CancellationToken cancellationToken)
    {
        byte[] record = BuildForcedRecord(bestUpdate, forceSlot);
        await AppendChainAsync(3, record, cancellationToken);
        RecordState(store);
    }

    private bool RequiresChainRecord(LightClientStore store) =>
        store.Period != _period || store.NextSyncCommitteeKnown != _nextKnown ||
        store.PreviousMaxParticipants != _previousMaxParticipants || store.CurrentMaxParticipants > _currentMaxParticipants;

    private async Task AppendChainAsync(byte kind, byte[] encoded, CancellationToken cancellationToken)
    {
        byte[] bootstrap = _bootstrap ?? throw new InvalidOperationException("The consensus journal is not initialized.");
        int additional = _latestHead is null ? 1 : 2;
        if (_updates.Count + additional > MaxUpdates)
            throw new InvalidDataException("Saved committee chain reached its update limit; supply a newer trusted checkpoint.");
        List<(byte Kind, byte[] Data)> updates = [.. _updates];
        if (_latestHead is { } head) updates.Add(head);
        updates.Add((kind, encoded));
        byte[] chain = BuildChain(bootstrap, updates);
        await WriteAtomicAsync(_chainPath, chain, cancellationToken);
        _updates.Clear();
        _updates.AddRange(updates);
        _chainDigest = chain.AsSpan(chain.Length - DigestBytes).ToArray();
        _latestHead = null;
    }

    private void RecordState(LightClientStore store)
    {
        _period = store.Period;
        _nextKnown = store.NextSyncCommitteeKnown;
        _slot = store.FinalizedHeader.Beacon!.Slot;
        _previousMaxParticipants = store.PreviousMaxParticipants;
        _currentMaxParticipants = store.CurrentMaxParticipants;
    }

    private byte[] BuildChain(byte[] bootstrap, IReadOnlyList<(byte Kind, byte[] Data)> updates)
    {
        using MemoryStream output = new();
        WritePrefix(output, 0);
        WriteRecord(output, bootstrap);
        WriteInt32(output, updates.Count);
        foreach ((byte kind, byte[] data) in updates)
        {
            output.WriteByte(kind);
            WriteRecord(output, data);
        }
        if (output.Length + DigestBytes > MaxChainBytes) throw new InvalidDataException("Saved committee chain exceeds its size limit; supply a newer trusted checkpoint.");
        return Finish(output);
    }

    private byte[] BuildHead(byte kind, byte[] update)
    {
        using MemoryStream output = new();
        WritePrefix(output, 1);
        output.Write(_chainDigest ?? throw new InvalidOperationException("The consensus journal is not initialized."));
        output.WriteByte(kind);
        WriteRecord(output, update);
        return Finish(output);
    }

    private byte[] BuildForcedRecord(LightClientUpdate bestUpdate, ulong forceSlot)
    {
        bool optimistic = bestUpdate.FinalityBranch is null && bestUpdate.NextSyncCommitteeBranch is null;
        byte[] encoded = optimistic
            ? LightClientWireCodec.EncodeOptimistic(new LightClientOptimisticUpdate
            {
                AttestedHeader = bestUpdate.AttestedHeader,
                SyncAggregate = bestUpdate.SyncAggregate,
                SignatureSlot = bestUpdate.SignatureSlot,
            }, _spec)
            : LightClientWireCodec.EncodeUpdate(bestUpdate, _spec);
        byte[] result = new byte[9 + encoded.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(result, forceSlot);
        result[8] = optimistic ? (byte)1 : (byte)0;
        encoded.CopyTo(result, 9);
        return result;
    }

    private void WritePrefix(Stream output, byte fileKind)
    {
        output.Write(Magic);
        output.WriteByte(1);
        output.WriteByte(fileKind);
        output.WriteByte(_network);
        output.Write(_checkpoint.Bytes);
    }

    private ReadOnlySpan<byte> VerifyEnvelope(byte[] file, byte expectedKind)
    {
        if (file.Length < 39 + DigestBytes) throw new InvalidDataException("Saved consensus data is truncated.");
        ReadOnlySpan<byte> payload = file.AsSpan(0, file.Length - DigestBytes);
        if (!payload[..4].SequenceEqual(Magic) || payload[4] != 1 || payload[5] != expectedKind
            || payload[6] != _network || !payload.Slice(7, 32).SequenceEqual(_checkpoint.Bytes))
            throw new InvalidDataException("Saved consensus data belongs to a different network or checkpoint.");
        Span<byte> digest = stackalloc byte[DigestBytes];
        SHA256.HashData(payload, digest);
        if (!digest.SequenceEqual(file.AsSpan(file.Length - DigestBytes)))
            throw new InvalidDataException("Saved consensus data is corrupt.");
        return payload;
    }

    private static byte[] Finish(MemoryStream output)
    {
        byte[] payload = output.ToArray();
        byte[] result = new byte[payload.Length + DigestBytes];
        payload.CopyTo(result, 0);
        SHA256.HashData(payload, result.AsSpan(payload.Length));
        return result;
    }

    private void ProcessRecord(LightClientStore store, byte kind, byte[] encoded, ulong currentSlot)
    {
        switch (kind)
        {
            case 1: store.Process(LightClientWireCodec.DecodeUpdate(encoded, _spec), currentSlot); break;
            case 2: store.Process(LightClientWireCodec.DecodeFinality(encoded, _spec), currentSlot); break;
            case 4: store.Process(LightClientWireCodec.DecodeOptimistic(encoded, _spec), currentSlot); break;
            case 3:
                if (encoded.Length < 10) throw new InvalidDataException("Saved forced update is truncated.");
                ulong forceSlot = BinaryPrimitives.ReadUInt64LittleEndian(encoded);
                if (forceSlot > currentSlot) throw new InvalidDataException("Saved forced update is in the future.");
                if (encoded[8] == 1)
                    store.Process(LightClientWireCodec.DecodeOptimistic(encoded.AsSpan(9), _spec), forceSlot);
                else if (encoded[8] == 0)
                    store.Process(LightClientWireCodec.DecodeUpdate(encoded.AsSpan(9), _spec), forceSlot);
                else throw new InvalidDataException("Saved forced update has an unknown type.");
                if (!store.ForceUpdate(forceSlot)) throw new InvalidDataException("Saved forced update cannot advance the sync period.");
                break;
            default: throw new InvalidDataException("Saved consensus data has an unknown update type.");
        }
    }

    private static void WriteRecord(Stream output, byte[] bytes)
    {
        if (bytes.Length is 0 or > MaxRecordBytes) throw new InvalidDataException("Saved consensus record has an invalid size.");
        WriteInt32(output, bytes.Length);
        output.Write(bytes);
    }

    private static byte[] ReadRecord(ReadOnlySpan<byte> data, ref int offset)
    {
        int length = ReadInt32(data, ref offset);
        if (length is <= 0 or > MaxRecordBytes || length > data.Length - offset)
            throw new InvalidDataException("Saved consensus record has an invalid size.");
        byte[] bytes = data.Slice(offset, length).ToArray();
        offset += length;
        return bytes;
    }

    private static void WriteInt32(Stream output, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        output.Write(buffer);
    }

    private static int ReadInt32(ReadOnlySpan<byte> data, ref int offset)
    {
        if (data.Length - offset < 4) throw new InvalidDataException("Saved consensus data is truncated.");
        int value = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));
        offset += 4;
        return value;
    }

    private static byte ReadByte(ReadOnlySpan<byte> data, ref int offset)
    {
        if (offset == data.Length) throw new InvalidDataException("Saved consensus data is truncated.");
        return data[offset++];
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, int maxBytes, CancellationToken cancellationToken)
    {
        FileInfo info = new(path);
        if (info.Length > maxBytes) throw new InvalidDataException("Saved consensus data exceeds its size limit.");
        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        if (bytes.Length > maxBytes) throw new InvalidDataException("Saved consensus data exceeds its size limit.");
        return bytes;
    }

    private static async Task WriteAtomicAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
