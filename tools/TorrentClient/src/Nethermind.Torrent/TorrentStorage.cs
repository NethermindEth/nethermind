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

internal sealed class TorrentStorage(TorrentMetadata metadata, string rootPath) : IAsyncDisposable
{
    private readonly TorrentMetadata _metadata = metadata;
    private readonly string _rootPath = rootPath;
    private readonly List<OpenFile> _files = [];

    private sealed class OpenFile(TorrentFileEntry entry, FileStream stream)
    {
        public TorrentFileEntry Entry { get; } = entry;

        public FileStream Stream { get; } = stream;
    }

    public async Task InitializeAsync(CancellationToken token)
    {
        Directory.CreateDirectory(_rootPath);
        for (int i = 0; i < _metadata.Files.Count; i++)
        {
            TorrentFileEntry entry = _metadata.Files[i];
            string path = GetFullPath(entry);
            string? directory = Path.GetDirectoryName(path);
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
            }

            FileStream? stream = null;
            try
            {
                stream = new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.Read,
                    bufferSize: 1 << 20,
                    FileOptions.Asynchronous | FileOptions.RandomAccess);

                if (stream.Length != entry.Length)
                {
                    stream.SetLength(entry.Length);
                }

                _files.Add(new OpenFile(entry, stream));
                stream = null;
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
        await ReadPieceAsync(pieceIndex, buffer.AsMemory(0, pieceSize), token);
        Span<byte> hash = stackalloc byte[TorrentMetadata.Sha1Length];
        SHA1.HashData(buffer.AsSpan(0, pieceSize), hash);
        return hash.SequenceEqual(_metadata.GetPieceHash(pieceIndex));
    }

    public async Task WritePieceAsync(int pieceIndex, ReadOnlyMemory<byte> data, CancellationToken token)
    {
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

            await RandomAccess.WriteAsync(file.Stream.SafeFileHandle, data.Slice(sourceOffset, byteCount), fileOffset, token);
            sourceOffset += byteCount;
            remaining -= byteCount;
        }

        if (remaining != 0)
        {
            throw new InvalidOperationException("Piece write did not cover the full payload range.");
        }
    }

    private async Task ReadPieceAsync(int pieceIndex, Memory<byte> destination, CancellationToken token)
    {
        long globalOffset = (long)pieceIndex * _metadata.PieceLength;
        int remaining = destination.Length;
        int destinationOffset = 0;
        for (int i = 0; i < _files.Count && remaining > 0; i++)
        {
            OpenFile file = _files[i];
            long currentGlobalOffset = globalOffset + destinationOffset;
            if (!Intersects(currentGlobalOffset, remaining, file.Entry, out long fileOffset, out int byteCount))
            {
                continue;
            }

            int readTotal = 0;
            while (readTotal < byteCount)
            {
                int read = await RandomAccess.ReadAsync(
                    file.Stream.SafeFileHandle,
                    destination.Slice(destinationOffset + readTotal, byteCount - readTotal),
                    fileOffset + readTotal,
                    token);
                if (read == 0)
                {
                    destination.Span.Slice(destinationOffset + readTotal, byteCount - readTotal).Clear();
                    break;
                }

                readTotal += read;
            }

            destinationOffset += byteCount;
            remaining -= byteCount;
        }

        if (remaining != 0)
        {
            throw new InvalidOperationException("Piece read did not cover the full payload range.");
        }
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
            await _files[i].Stream.DisposeAsync();
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
    private readonly bool[] _pieces = new bool[pieceCount];

    public bool IsEmpty { get; private set; } = true;

    public void SetAll()
    {
        for (int i = 0; i < _pieces.Length; i++)
        {
            _pieces[i] = true;
        }

        IsEmpty = false;
    }

    public void SetPiece(int index)
    {
        if ((uint)index < (uint)_pieces.Length)
        {
            _pieces[index] = true;
            IsEmpty = false;
        }
    }

    public void ReadBitfield(ReadOnlySpan<byte> bytes)
    {
        for (int i = 0; i < _pieces.Length; i++)
        {
            int byteIndex = i / 8;
            if (byteIndex >= bytes.Length)
            {
                break;
            }

            int bit = 7 - i % 8;
            _pieces[i] = (bytes[byteIndex] & (1 << bit)) != 0;
            IsEmpty = false;
        }
    }

    public bool HasPiece(int index) => (uint)index < (uint)_pieces.Length && _pieces[index];
}
