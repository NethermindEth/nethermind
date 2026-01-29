// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using LevelDB;
using Nethermind.Core;
using Nethermind.Db;

namespace Nethermind.Xdc.Migration;

public sealed class LevelReadOnlyDbAdapter(DB levelDb) : IDb
{
    // TODO: create a "fixed" nuget package and remove this
    private static IntPtr Resolver(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!name.Equals("leveldb.dll", StringComparison.Ordinal))
            return nint.Zero;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            name = "libleveldb-snappy.so";
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            name = "libleveldb-snappy.dylib";
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            name = "leveldb-snappy.dll";
        else
            throw new PlatformNotSupportedException();

        return NativeLibrary.Load(name, assembly, null);
    }

    static LevelReadOnlyDbAdapter()
    {
        NativeLibrary.SetDllImportResolver(typeof(LevelDBInterop).Assembly, Resolver);
    }

    private readonly DB _levelDb = levelDb ?? throw new ArgumentNullException(nameof(levelDb));

    public string Name => "LevelDb";

    public byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) => _levelDb.Get(key.ToArray());

    public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
    {
        if (value is null)
            _levelDb.Delete(key.ToArray());
        else
            _levelDb.Put(key.ToArray(), value);
    }

    public KeyValuePair<byte[], byte[]?>[] this[byte[][] keys]
    {
        get
        {
            var result = new KeyValuePair<byte[], byte[]?>[keys.Length];
            for (int i = 0; i < keys.Length; i++)
                result[i] = new(keys[i], _levelDb.Get(keys[i]));

            return result;
        }
    }

    public IEnumerable<KeyValuePair<byte[], byte[]?>> GetAll(bool ordered = false)
    {
        foreach (var (key, value) in _levelDb)
            yield return new(key, value);
    }

    public IEnumerable<byte[]> GetAllKeys(bool ordered = false)
    {
        foreach (var (key, _) in _levelDb)
            yield return key;
    }

    public IEnumerable<byte[]> GetAllValues(bool ordered = false)
    {
        foreach (var (_, value) in _levelDb)
            yield return value;
    }

    public IWriteBatch StartWriteBatch() => throw new NotSupportedException("Read-only LevelDB adapter.");

    public void Flush(bool onlyWal = false) => throw new NotSupportedException("Read-only LevelDB adapter.");
    public void Clear() => throw new NotSupportedException("Read-only LevelDB adapter.");
    public void Compact() => throw new NotSupportedException("Read-only LevelDB adapter.");
    public void Dispose() { _levelDb.Dispose(); }
}
