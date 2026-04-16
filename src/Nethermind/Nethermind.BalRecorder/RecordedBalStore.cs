// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.IO;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.Eip7928;

namespace Nethermind.BalRecorder;

/// <summary>
/// Stores recorded BALs in era files — one file per 8192-block range.
/// File layout: 65536-byte header (8192 × 8 bytes: uint32 offset + uint32 size per slot),
/// followed by appended RLP-encoded BAL data. Offset 0 means no entry.
/// </summary>
public class RecordedBalStore(string directory, bool replayEnabled, bool recordingEnabled) : IRecordedBalStore
{
    public bool ReplayEnabled => replayEnabled;
    public bool RecordingEnabled => recordingEnabled;

    private const int EraSize = 8192;
    private const int HeaderSize = EraSize * 8; // 65536 bytes

    private string FilePath(long blockNumber) =>
        Path.Combine(directory, $"{blockNumber / EraSize:D8}.bal");

    public void Insert(Block block, BlockAccessList bal)
    {
        Directory.CreateDirectory(directory);
        string path = FilePath(block.Number);
        int slot = (int)(block.Number % EraSize);

        using NettyRlpStream rlp = BlockAccessListDecoder.Instance.EncodeToNewNettyStream(bal);
        ReadOnlySpan<byte> data = rlp.AsSpan();

        using FileStream fs = new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);

        if (fs.Length == 0)
        {
            fs.Write(new byte[HeaderSize]);
        }

        fs.Seek(0, SeekOrigin.End);
        uint offset = (uint)fs.Position;
        fs.Write(data);

        Span<byte> entry = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(entry, offset);
        BinaryPrimitives.WriteUInt32BigEndian(entry[4..], (uint)data.Length);
        fs.Seek(slot * 8, SeekOrigin.Begin);
        fs.Write(entry);
    }

    public BlockAccessList? Get(long blockNumber, Hash256 blockHash)
    {
        string path = FilePath(blockNumber);
        if (!File.Exists(path)) return null;

        int slot = (int)(blockNumber % EraSize);

        using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        Span<byte> entry = stackalloc byte[8];
        fs.Seek(slot * 8, SeekOrigin.Begin);
        fs.ReadExactly(entry);

        uint offset = BinaryPrimitives.ReadUInt32BigEndian(entry);
        if (offset == 0) return null;

        uint size = BinaryPrimitives.ReadUInt32BigEndian(entry[4..]);
        byte[] data = new byte[size];
        fs.Seek(offset, SeekOrigin.Begin);
        fs.ReadExactly(data);

        return BlockAccessListDecoder.Instance.Decode(data);
    }
}

public class NullRecordedBalStore : IRecordedBalStore
{
    public static NullRecordedBalStore Instance { get; } = new();
    public void Insert(Block block, BlockAccessList bal) { }
    public BlockAccessList? Get(long blockNumber, Hash256 blockHash) => null;
    public bool ReplayEnabled => false;
    public bool RecordingEnabled => false;
}
