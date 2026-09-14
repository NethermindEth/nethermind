// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Pbt.Image;

/// <summary>Fixed-record external sort with bounded buffers and two-way disk merges.</summary>
internal sealed class PbtOfflineSourceSort(string directory, int recordLength, int keyLength, int bufferBytes,
    CancellationToken cancellationToken) : IDisposable
{
    private readonly List<byte[]> _buffer = [];
    private readonly string?[] _levels = new string?[64];
    private readonly int _capacity = Math.Max(1, bufferBytes / (recordLength + 64));
    private string? _sorted;

    public void Add(byte[] record)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (record.Length != recordLength) throw new ArgumentException("Wrong sort record size.", nameof(record));
        if (_sorted is not null) throw new InvalidOperationException("Sort already completed.");
        _buffer.Add(record);
        if (_buffer.Count == _capacity) Flush();
    }

    public IEnumerable<byte[]> Read()
    {
        Complete();
        using FileStream stream = File.OpenRead(_sorted!);
        while (ReadRecord(stream) is { } record) yield return record;
    }

    private byte[]? ReadRecord(Stream stream)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int first = stream.ReadByte();
        if (first < 0) return null;
        byte[] record = new byte[recordLength];
        record[0] = (byte)first;
        stream.ReadExactly(record.AsSpan(1));
        return record;
    }

    private int Compare(byte[] left, byte[] right) => left.AsSpan(0, keyLength).SequenceCompareTo(right.AsSpan(0, keyLength));
    private string NewPath() => Path.Combine(directory, Guid.NewGuid().ToString("N"));

    private void Flush()
    {
        cancellationToken.ThrowIfCancellationRequested();
        _buffer.Sort(Compare);
        string run = NewPath();
        using (FileStream output = File.Create(run))
            foreach (byte[] record in _buffer) output.Write(record);
        _buffer.Clear();
        for (int level = 0; level < _levels.Length; level++)
        {
            if (_levels[level] is not { } previous)
            {
                _levels[level] = run;
                return;
            }
            run = Merge(previous, run);
            _levels[level] = null;
        }
        throw new IOException("Offline sort run count overflow.");
    }

    private string Merge(string leftPath, string rightPath)
    {
        string result = NewPath();
        using (FileStream left = File.OpenRead(leftPath))
        using (FileStream right = File.OpenRead(rightPath))
        using (FileStream output = File.Create(result))
        {
            byte[]? leftRecord = ReadRecord(left), rightRecord = ReadRecord(right);
            while (leftRecord is not null || rightRecord is not null)
            {
                if (rightRecord is null || leftRecord is not null && Compare(leftRecord, rightRecord) <= 0)
                {
                    output.Write(leftRecord!);
                    leftRecord = ReadRecord(left);
                }
                else
                {
                    output.Write(rightRecord);
                    rightRecord = ReadRecord(right);
                }
            }
        }
        File.Delete(leftPath);
        File.Delete(rightPath);
        return result;
    }

    private void Complete()
    {
        if (_sorted is not null) return;
        if (_buffer.Count != 0) Flush();
        for (int level = 0; level < _levels.Length; level++)
        {
            if (_levels[level] is not { } run) continue;
            _sorted = _sorted is null ? run : Merge(_sorted, run);
            _levels[level] = null;
        }
        if (_sorted is null)
        {
            _sorted = NewPath();
            using FileStream empty = File.Create(_sorted);
        }
    }

    public void Dispose()
    {
        _buffer.Clear();
        if (_sorted is not null) File.Delete(_sorted);
        foreach (string? run in _levels)
            if (run is not null) File.Delete(run);
    }
}
