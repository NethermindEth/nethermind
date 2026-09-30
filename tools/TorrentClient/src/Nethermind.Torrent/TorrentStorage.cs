// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;

namespace Nethermind.Torrent;

internal enum PieceState
{
    Missing,
    Reserved,
    Complete,
}

internal sealed class TorrentStorage(TorrentMetadata metadata, string rootPath, bool readOnly = false) : IAsyncDisposable
{
    private readonly TorrentMetadata _metadata = metadata;
    private readonly string _rootPath = rootPath;
    private readonly List<OpenFile> _files = [];

    private sealed class OpenFile(TorrentFileEntry entry, FileStream? stream)
    {
        public TorrentFileEntry Entry { get; } = entry;

        public FileStream? Stream { get; } = stream;
    }

    public async Task InitializeAsync(CancellationToken token)
    {
        if (!readOnly)
        {
            Directory.CreateDirectory(_rootPath);
        }

        for (int i = 0; i < _metadata.Files.Count; i++)
        {
            TorrentFileEntry entry = _metadata.Files[i];
            string path = GetFullPath(entry);
            string? directory = Path.GetDirectoryName(path);
            if (!readOnly && directory is not null)
            {
                Directory.CreateDirectory(directory);
            }

            FileStream? stream = null;
            try
            {
                stream = new FileStream(
                    path,
                    readOnly ? FileMode.Open : FileMode.OpenOrCreate,
                    readOnly ? FileAccess.Read : FileAccess.ReadWrite,
                    readOnly ? FileShare.ReadWrite : FileShare.Read,
                    bufferSize: 1 << 20,
                    FileOptions.Asynchronous | FileOptions.RandomAccess);

                if (!readOnly && stream.Length != entry.Length)
                {
                    stream.SetLength(entry.Length);
                }

                _files.Add(new OpenFile(entry, stream));
                stream = null;
            }
            catch (FileNotFoundException) when (readOnly)
            {
                _files.Add(new OpenFile(entry, null));
            }
            catch (DirectoryNotFoundException) when (readOnly)
            {
                _files.Add(new OpenFile(entry, null));
            }
            finally
            {
                if (stream is not null)
                {
                    await stream.DisposeAsync();
                }
            }
        }

        await Task.CompletedTask;
        token.ThrowIfCancellationRequested();
    }

    public async Task<bool> VerifyPieceAsync(int pieceIndex, byte[] buffer, CancellationToken token)
    {
        int pieceSize = _metadata.GetPieceSize(pieceIndex);
        if (!await ReadRangeAsync((long)pieceIndex * _metadata.PieceLength, buffer.AsMemory(0, pieceSize), token))
        {
            return false;
        }

        Span<byte> hash = stackalloc byte[TorrentMetadata.Sha1Length];
        SHA1.HashData(buffer.AsSpan(0, pieceSize), hash);
        return hash.SequenceEqual(_metadata.GetPieceHash(pieceIndex));
    }

    public Task<bool> ReadBlockAsync(int pieceIndex, int begin, Memory<byte> destination, CancellationToken token)
    {
        int pieceSize = _metadata.GetPieceSize(pieceIndex);
        if (begin < 0 || destination.Length == 0 || begin > pieceSize - destination.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(begin), "Block is outside the requested piece.");
        }

        return ReadRangeAsync((long)pieceIndex * _metadata.PieceLength + begin, destination, token);
    }

    public async Task WritePieceAsync(int pieceIndex, ReadOnlyMemory<byte> data, CancellationToken token)
    {
        if (readOnly)
        {
            throw new InvalidOperationException("Read-only torrent storage cannot write pieces.");
        }

        if (data.Length != _metadata.GetPieceSize(pieceIndex))
        {
            throw new ArgumentException("Piece buffer length does not match torrent metadata.", nameof(data));
        }

        long globalOffset = (long)pieceIndex * _metadata.PieceLength;
        int remaining = data.Length;
        int sourceOffset = 0;
        for (int i = 0; i < _files.Count && remaining > 0; i++)
        {
            OpenFile file = _files[i];
            long currentGlobalOffset = globalOffset + sourceOffset;
            if (!Intersects(currentGlobalOffset, remaining, file.Entry, out long fileOffset, out int byteCount))
            {
                continue;
            }

            await RandomAccess.WriteAsync(file.Stream!.SafeFileHandle, data.Slice(sourceOffset, byteCount), fileOffset, token);
            sourceOffset += byteCount;
            remaining -= byteCount;
        }

        if (remaining != 0)
        {
            throw new InvalidOperationException("Piece write did not cover the full payload range.");
        }
    }

    private async Task<bool> ReadRangeAsync(long globalOffset, Memory<byte> destination, CancellationToken token)
    {
        int remaining = destination.Length;
        int destinationOffset = 0;
        bool fullyRead = true;
        for (int i = 0; i < _files.Count && remaining > 0; i++)
        {
            OpenFile file = _files[i];
            long currentGlobalOffset = globalOffset + destinationOffset;
            if (!Intersects(currentGlobalOffset, remaining, file.Entry, out long fileOffset, out int byteCount))
            {
                continue;
            }

            FileStream? stream = file.Stream;
            if (stream is null)
            {
                destination.Span.Slice(destinationOffset, byteCount).Clear();
                fullyRead = false;
            }
            else
            {
                int readTotal = 0;
                while (readTotal < byteCount)
                {
                    int read = await RandomAccess.ReadAsync(
                        stream.SafeFileHandle,
                        destination.Slice(destinationOffset + readTotal, byteCount - readTotal),
                        fileOffset + readTotal,
                        token);
                    if (read == 0)
                    {
                        destination.Span.Slice(destinationOffset + readTotal, byteCount - readTotal).Clear();
                        fullyRead = false;
                        break;
                    }

                    readTotal += read;
                }
            }

            destinationOffset += byteCount;
            remaining -= byteCount;
        }

        if (remaining != 0)
        {
            throw new InvalidOperationException("Piece read did not cover the full payload range.");
        }

        return fullyRead;
    }

    private static bool Intersects(long globalOffset, int count, TorrentFileEntry file, out long fileOffset, out int byteCount)
    {
        long rangeEnd = globalOffset + count;
        long fileStart = file.Offset;
        long fileEnd = file.Offset + file.Length;
        long start = Math.Max(globalOffset, fileStart);
        long end = Math.Min(rangeEnd, fileEnd);
        if (start >= end)
        {
            fileOffset = 0;
            byteCount = 0;
            return false;
        }

        fileOffset = start - fileStart;
        byteCount = checked((int)(end - start));
        return true;
    }

    private string GetFullPath(TorrentFileEntry entry)
    {
        string fullPath = Path.GetFullPath(Path.Combine(_rootPath, entry.Path));
        string fullRoot = Path.GetFullPath(_rootPath);
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Torrent file path escapes the output directory: {entry.Path}");
        }

        return fullPath;
    }

    public async ValueTask DisposeAsync()
    {
        for (int i = 0; i < _files.Count; i++)
        {
            if (_files[i].Stream is FileStream stream)
            {
                await stream.DisposeAsync();
            }
        }
    }
}

internal sealed class PiecePicker(TorrentMetadata metadata)
{
    private readonly TorrentMetadata _metadata = metadata;
    private readonly Lock _lock = new();
    private int _completedPieces;

    private readonly PieceState[] _states = new PieceState[metadata.PieceCount];

    public int CompletedPieces
    {
        get
        {
            lock (_lock)
            {
                return _completedPieces;
            }
        }
    }

    public long DownloadedBytes
    {
        get
        {
            lock (_lock)
            {
                long downloaded = 0;
                for (int i = 0; i < _states.Length; i++)
                {
                    if (_states[i] == PieceState.Complete)
                    {
                        downloaded += _metadata.GetPieceSize(i);
                    }
                }

                return downloaded;
            }
        }
    }

    public bool IsComplete
    {
        get
        {
            lock (_lock)
            {
                return _completedPieces == _states.Length;
            }
        }
    }

    public bool IsPieceComplete(int pieceIndex)
    {
        lock (_lock)
        {
            return (uint)pieceIndex < (uint)_states.Length && _states[pieceIndex] == PieceState.Complete;
        }
    }

    public byte[] GetCompletedBitfield()
    {
        lock (_lock)
        {
            byte[] bitfield = new byte[_states.Length / 8 + (_states.Length % 8 == 0 ? 0 : 1)];
            for (int i = 0; i < _states.Length; i++)
            {
                if (_states[i] == PieceState.Complete)
                {
                    bitfield[i / 8] |= (byte)(1 << (7 - i % 8));
                }
            }

            return bitfield;
        }
    }

    public void MarkComplete(int pieceIndex)
    {
        lock (_lock)
        {
            if (_states[pieceIndex] != PieceState.Complete)
            {
                _states[pieceIndex] = PieceState.Complete;
                _completedPieces++;
            }
        }
    }

    public void Release(int pieceIndex)
    {
        lock (_lock)
        {
            if (_states[pieceIndex] == PieceState.Reserved)
            {
                _states[pieceIndex] = PieceState.Missing;
            }
        }
    }

    public bool TryReserve(PeerBitfield peerPieces, out int pieceIndex)
    {
        lock (_lock)
        {
            int best = -1;
            for (int i = 0; i < _states.Length; i++)
            {
                if (_states[i] == PieceState.Missing && peerPieces.HasPiece(i))
                {
                    best = i;
                    break;
                }
            }

            if (best < 0)
            {
                pieceIndex = -1;
                return false;
            }

            _states[best] = PieceState.Reserved;
            pieceIndex = best;
            return true;
        }
    }

    public bool HasReservablePiece(PeerBitfield peerPieces)
    {
        lock (_lock)
        {
            for (int i = 0; i < _states.Length; i++)
            {
                if (_states[i] == PieceState.Missing && peerPieces.HasPiece(i))
                {
                    return true;
                }
            }

            return false;
        }
    }
}

internal sealed class PeerBitfield(int pieceCount)
{
    private readonly byte[] _pieces = new byte[(pieceCount + 7) / 8];
    private readonly int _pieceCount = pieceCount;
    private readonly Lock _lock = new();
    private bool _isEmpty = true;
    private bool _hasInventory;

    public bool IsEmpty
    {
        get => Volatile.Read(ref _isEmpty);
    }

    public void SetAll()
    {
        lock (_lock)
        {
            Array.Fill(_pieces, byte.MaxValue);
            MaskUnusedBits();
            Volatile.Write(ref _isEmpty, _pieceCount == 0);
            _hasInventory = true;
        }
    }

    public void SetPiece(int index)
    {
        lock (_lock)
        {
            if ((uint)index < (uint)_pieceCount)
            {
                int byteIndex = index / 8;
                _pieces[byteIndex] |= (byte)(1 << (7 - index % 8));
                Volatile.Write(ref _isEmpty, false);
                _hasInventory = true;
            }
        }
    }

    public void ReadBitfield(ReadOnlySpan<byte> bytes)
    {
        lock (_lock)
        {
            _hasInventory = true;
            Array.Clear(_pieces);
            bytes[..Math.Min(bytes.Length, _pieces.Length)].CopyTo(_pieces);
            MaskUnusedBits();
            bool isEmpty = true;
            for (int i = 0; i < _pieces.Length; i++)
            {
                isEmpty &= _pieces[i] == 0;
            }

            Volatile.Write(ref _isEmpty, isEmpty);
        }
    }

    public bool HasPiece(int index) => (uint)index < (uint)_pieceCount &&
        (_pieces[index / 8] & (1 << (7 - index % 8))) != 0;

    public bool AddTo(byte[] bitfield)
    {
        lock (_lock)
        {
            for (int i = 0; i < _pieces.Length; i++)
            {
                bitfield[i] |= _pieces[i];
            }

            return _hasInventory;
        }
    }

    private void MaskUnusedBits()
    {
        int remainder = _pieceCount % 8;
        if (remainder != 0)
        {
            _pieces[^1] &= (byte)(0xff << (8 - remainder));
        }
    }
}
