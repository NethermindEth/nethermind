// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text;
using Nethermind.Core.Crypto;
using Nethermind.Network;
using Nethermind.Network.P2P.Messages;
using Nethermind.Serialization.FluentRlp;

namespace Nethermind.Arkiv.Sync;

/// <summary>
/// <c>arkiv/1</c>: a peer's GolemDB store, fetched by a node that state-syncs. Snap carries the account trie, which on
/// Arkiv is neither the header's state root nor the state; the state is GolemDB, so the store itself is what is sent:
/// the files of a copy taken at one block, in chunks. Each message is an RLP list starting with a request id.
/// </summary>
public static class ArkivSyncProtocol
{
    public const string Code = "arkiv";
    public const byte Version = 1;

    public const int GetStoreManifest = 0x00;
    public const int StoreManifest = 0x01;
    public const int GetStoreChunk = 0x02;
    public const int StoreChunk = 0x03;

    /// <summary>
    /// The most bytes a chunk carries. Nethermind stops writing to a peer whose outbound buffer passes 3 MB and drops what
    /// it would send meanwhile, so a chunk, times the chunks a node keeps in flight, stays under that.
    /// </summary>
    public const int MaxChunk = 1024 * 1024;

    /// <summary>The most files a manifest lists (a GolemDB store has a handful).</summary>
    public const int MaxFiles = 4096;
}

public abstract class ArkivSyncMessage : P2PMessage
{
    public override string Protocol => ArkivSyncProtocol.Code;
    public long RequestId { get; set; } = Random.Shared.NextInt64();
}

/// <summary>Asks for the copy the peer serves; one older than block <see cref="MinNumber"/> is of no use to the asker.</summary>
public sealed class GetStoreManifestMessage : ArkivSyncMessage
{
    public override int PacketType => ArkivSyncProtocol.GetStoreManifest;
    public ulong MinNumber { get; set; }
}

/// <summary>
/// The copy the peer serves: the block its head commit is tagged with, and its files. <see cref="Number"/> 0 means none
/// yet, and a copy older than the request's minimum is being replaced: ask again shortly.
/// </summary>
public sealed class StoreManifestMessage : ArkivSyncMessage
{
    public override int PacketType => ArkivSyncProtocol.StoreManifest;
    public ulong Number { get; set; }
    public Hash256 Hash { get; set; } = Keccak.Zero;
    public IReadOnlyList<(string Path, long Size)> Files { get; set; } = [];
}

/// <summary>Asks for <see cref="Length"/> bytes at <see cref="Offset"/> of file <see cref="File"/> of the copy at block <see cref="Hash"/>.</summary>
public sealed class GetStoreChunkMessage : ArkivSyncMessage
{
    public override int PacketType => ArkivSyncProtocol.GetStoreChunk;
    public Hash256 Hash { get; set; } = Keccak.Zero;
    public int File { get; set; }
    public long Offset { get; set; }
    public int Length { get; set; }
}

/// <summary>The bytes asked for; empty when the peer no longer has that copy.</summary>
public sealed class StoreChunkMessage : ArkivSyncMessage
{
    public override int PacketType => ArkivSyncProtocol.StoreChunk;
    public byte[] Data { get; set; } = [];
}

public abstract class ArkivSyncSerializer<T> : IMessageSerializer<T> where T : ArkivSyncMessage
{
    public int GetLength(T message)
    {
        RlpWriter writer = RlpWriter.LengthWriter();
        writer.WriteSequence((this, message), WriteMessage);
        return writer.Length;
    }

    public int Serialize(Span<byte> destination, T message)
    {
        RlpWriter writer = RlpWriter.ContentWriter(destination);
        writer.WriteSequence((this, message), WriteMessage);
        return writer.Length;
    }

    public T Deserialize(ReadOnlySpan<byte> data)
    {
        RlpReader reader = new(data);
        T message = reader.ReadSequence(this, static (scoped ref RlpReader r, ArkivSyncSerializer<T> serializer) =>
        {
            long requestId = r.ReadInteger<long>();
            T read = serializer.Read(ref r);
            read.RequestId = requestId;
            return read;
        });
        if (reader.HasNext) throw new RlpReaderException($"{typeof(T).Name}: trailing bytes");
        return message;
    }

    private static void WriteMessage(ref RlpWriter writer, (ArkivSyncSerializer<T> Serializer, T Message) context)
    {
        writer.Write(context.Message.RequestId);
        context.Serializer.WriteBody(ref writer, context.Message);
    }

    /// <summary>Reads the fields after the request id.</summary>
    protected abstract T Read(scoped ref RlpReader reader);

    /// <summary>Writes the fields after the request id.</summary>
    protected abstract void WriteBody(ref RlpWriter writer, T message);
}

public sealed class GetStoreManifestMessageSerializer : ArkivSyncSerializer<GetStoreManifestMessage>
{
    protected override GetStoreManifestMessage Read(scoped ref RlpReader reader) => new() { MinNumber = reader.ReadInteger<ulong>() };
    protected override void WriteBody(ref RlpWriter writer, GetStoreManifestMessage message) => writer.Write(message.MinNumber);
}

public sealed class StoreManifestMessageSerializer : ArkivSyncSerializer<StoreManifestMessage>
{
    protected override StoreManifestMessage Read(scoped ref RlpReader reader)
    {
        ulong number = reader.ReadInteger<ulong>();
        Hash256 hash = new(reader.ReadBytes(Hash256.Size));
        List<(string, long)> files = [];
        RlpReader list = reader.EnterSequence(out int listLength);
        while (list.HasNext)
        {
            if (files.Count == ArkivSyncProtocol.MaxFiles) throw new RlpLimitException($"a store manifest lists more than {ArkivSyncProtocol.MaxFiles} files");
            RlpReader file = list.EnterSequence(out int fileLength);
            string path = Encoding.UTF8.GetString(file.ReadBytes());
            long size = file.ReadInteger<long>();
            list.ExitSequence(in file, fileLength);
            files.Add((path, size));
        }
        reader.ExitSequence(in list, listLength);
        return new StoreManifestMessage { Number = number, Hash = hash, Files = files };
    }

    protected override void WriteBody(ref RlpWriter writer, StoreManifestMessage message)
    {
        writer.Write(message.Number);
        writer.Write(message.Hash.Bytes);
        writer.WriteSequence(message.Files, static (ref RlpWriter w, IReadOnlyList<(string Path, long Size)> files) =>
        {
            foreach ((string path, long size) in files)
            {
                w.WriteSequence((path, size), static (ref RlpWriter f, (string Path, long Size) file) =>
                {
                    f.Write(Encoding.UTF8.GetBytes(file.Path));
                    f.Write(file.Size);
                });
            }
        });
    }
}

public sealed class GetStoreChunkMessageSerializer : ArkivSyncSerializer<GetStoreChunkMessage>
{
    protected override GetStoreChunkMessage Read(scoped ref RlpReader reader) => new()
    {
        Hash = new Hash256(reader.ReadBytes(Hash256.Size)),
        File = reader.ReadInteger<int>(),
        Offset = reader.ReadInteger<long>(),
        Length = reader.ReadInteger<int>(),
    };

    protected override void WriteBody(ref RlpWriter writer, GetStoreChunkMessage message)
    {
        writer.Write(message.Hash.Bytes);
        writer.Write(message.File);
        writer.Write(message.Offset);
        writer.Write(message.Length);
    }
}

public sealed class StoreChunkMessageSerializer : ArkivSyncSerializer<StoreChunkMessage>
{
    protected override StoreChunkMessage Read(scoped ref RlpReader reader)
    {
        ReadOnlySpan<byte> data = reader.ReadBytes();
        if (data.Length > ArkivSyncProtocol.MaxChunk) throw new RlpLimitException($"a store chunk of {data.Length} bytes is over {ArkivSyncProtocol.MaxChunk}");
        return new StoreChunkMessage { Data = data.ToArray() };
    }

    protected override void WriteBody(ref RlpWriter writer, StoreChunkMessage message) => writer.Write(message.Data);
}
