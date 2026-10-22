// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using Nethermind.Crypto;

namespace SendBlobs;

public sealed class PendingKeyFile : IDisposable
{
    private const int KeyHexLengthWithPrefix = 66;
    private const int LineLength = KeyHexLengthWithPrefix + 1;
    private const byte NewLine = (byte)'\n';
    private const string PendingSuffix = ".pending";

    private readonly string _targetPath;
    private readonly string _tempPath;
    private readonly FileStream _stream;
    private bool _committed;
    private bool _disposed;

    private PendingKeyFile(string targetPath, string tempPath, FileStream stream)
    {
        _targetPath = targetPath;
        _tempPath = tempPath;
        _stream = stream;
    }

    public string TempPath => _tempPath;

    public static PendingKeyFile Open(string targetPath)
    {
        string tempPath = targetPath + PendingSuffix;

        FileStream stream;
        try
        {
            stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, bufferSize: 4096, FileOptions.None);
        }
        catch (IOException) when (File.Exists(tempPath))
        {
            throw new IOException(
                $"Pending key file already exists at '{tempPath}'. " +
                "A previous distribute run did not complete cleanly. " +
                "Inspect the file (it may contain private keys with funds), " +
                "merge or remove it, then retry.");
        }

        return new PendingKeyFile(targetPath, tempPath, stream);
    }

    public void AppendDurable(PrivateKey key)
    {
        if (_committed) throw new InvalidOperationException("PendingKeyFile has already been committed.");
        ObjectDisposedException.ThrowIf(_disposed, this);

        Span<byte> line = stackalloc byte[LineLength];
        string hex = key.ToString();
        int written = Encoding.ASCII.GetBytes(hex, line);
        if (written != KeyHexLengthWithPrefix)
            throw new InvalidOperationException($"Unexpected PrivateKey.ToString length: expected {KeyHexLengthWithPrefix}, got {written}.");
        line[KeyHexLengthWithPrefix] = NewLine;

        _stream.Write(line);
        _stream.Flush(flushToDisk: true);
    }

    public void CommitAtomic()
    {
        if (_committed) throw new InvalidOperationException("PendingKeyFile has already been committed.");
        ObjectDisposedException.ThrowIf(_disposed, this);

        _stream.Dispose();
        File.Move(_tempPath, _targetPath, overwrite: true);
        _committed = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_committed) return;
        _stream.Dispose();
    }
}
