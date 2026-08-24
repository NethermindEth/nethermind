// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Compression;
using ZstdSharp;

namespace Nethermind.Tools.Kute.Replay;

/// <summary>
/// Reads newline-delimited UTF-8 records from a plain, gzip- or zstd-compressed trace file.
/// </summary>
/// <remarks>
/// The captures this targets hold records with a median size of a few hundred kilobytes, so records
/// are surfaced as spans over an internal buffer rather than decoded strings: decoding to UTF-16
/// would double the footprint and the bytes are sent back out as UTF-8 anyway. The span handed out
/// by <see cref="TryReadRecord"/> is invalidated by the next call, so a caller that retains a record
/// beyond that must copy it. Compression is inferred from the file extension.
/// <para>
/// A zstd capture is typically a single frame, which cannot be seeked or decoded in parallel;
/// re-reading from the start is the only way to replay it.
/// </para>
/// </remarks>
public sealed class TraceLineReader : IDisposable
{
    private const int InitialBufferSize = 1 << 20;
    private const int MaxBufferSize = 1 << 28;

    private readonly Stream _file;
    private readonly Stream _records;
    private byte[] _buffer;
    private int _start;
    private int _end;
    private bool _endOfStream;

    /// <summary>Opens a trace file, transparently decompressing <c>.zst</c>, <c>.zstd</c> and <c>.gz</c>.</summary>
    /// <param name="path">Path to the trace file.</param>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    public TraceLineReader(string path)
    {
        _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, InitialBufferSize, FileOptions.SequentialScan);
        try
        {
            _records = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".zst" or ".zstd" => new DecompressionStream(_file),
                ".gz" => new GZipStream(_file, CompressionMode.Decompress),
                _ => _file,
            };
        }
        catch
        {
            _file.Dispose();
            throw;
        }

        _buffer = new byte[InitialBufferSize];
    }

    /// <summary>Number of records returned so far.</summary>
    public long RecordsRead { get; private set; }

    /// <summary>Compressed bytes consumed so far, for progress reporting.</summary>
    public long CompressedBytesRead => _file.Position;

    /// <summary>
    /// Reads the next record, excluding its line terminator.
    /// </summary>
    /// <param name="record">The record bytes, valid only until the next call.</param>
    /// <returns><see langword="true"/> if a record was read; <see langword="false"/> at end of file.</returns>
    /// <exception cref="InvalidDataException">A single record exceeds <see cref="MaxBufferSize"/>.</exception>
    public bool TryReadRecord(out ReadOnlySpan<byte> record)
    {
        while (true)
        {
            ReadOnlySpan<byte> buffered = _buffer.AsSpan(_start, _end - _start);
            int newline = buffered.IndexOf((byte)'\n');
            if (newline >= 0)
            {
                int consumed = _start + newline + 1;
                record = Trim(buffered[..newline]);
                _start = consumed;
                if (record.Length == 0)
                {
                    continue;
                }

                RecordsRead++;
                return true;
            }

            if (_endOfStream)
            {
                // A capture may lack the trailing newline, so the tail is still a record.
                record = Trim(buffered);
                _start = _end;
                if (record.Length == 0)
                {
                    return false;
                }

                RecordsRead++;
                return true;
            }

            Fill();
        }

        static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> span) =>
            span.Length > 0 && span[^1] == (byte)'\r' ? span[..^1] : span;
    }

    private void Fill()
    {
        if (_start > 0)
        {
            _buffer.AsSpan(_start, _end - _start).CopyTo(_buffer);
            _end -= _start;
            _start = 0;
        }

        if (_end == _buffer.Length)
        {
            if (_buffer.Length >= MaxBufferSize)
            {
                throw new InvalidDataException($"Trace record at index {RecordsRead} exceeds {MaxBufferSize} bytes.");
            }

            Array.Resize(ref _buffer, _buffer.Length * 2);
        }

        int read = _records.Read(_buffer, _end, _buffer.Length - _end);
        if (read == 0)
        {
            _endOfStream = true;
        }

        _end += read;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!ReferenceEquals(_records, _file))
        {
            _records.Dispose();
        }

        _file.Dispose();
    }
}
