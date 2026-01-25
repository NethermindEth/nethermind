// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Reflection;
using System.Runtime.InteropServices;
using LevelDB;
using Nethermind.Core;
using Nethermind.Db;

namespace Xdc;

public sealed class ReadOnlyLevelDb: IDb
{
    // TODO: handle non-Windows platforms
    // TODO: create a "fixed" nuget package and remove this?
    private static IntPtr Resolver(string libraryName, Assembly assembly, DllImportSearchPath? searchPath) =>
        string.Equals(libraryName, "leveldb.dll", StringComparison.OrdinalIgnoreCase) && NativeLibrary.TryLoad("leveldb-snappy.dll", out var handle)
            ? handle
            : IntPtr.Zero;

    static ReadOnlyLevelDb() => NativeLibrary.SetDllImportResolver(typeof(LevelDBInterop).Assembly, Resolver);

    public string Name => "LevelDb";

    private readonly DB _levelDb;

    // ReSharper disable once PrivateFieldCanBeConvertedToLocalVariable
    // force keep-alive to prevent destructor run beforehand
    private readonly Options _options;

    public ReadOnlyLevelDb(string path)
    {
        _options = new Options{CreateIfMissing = false};
        _levelDb = new DB(_options, path);
    }

    public byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) => _levelDb.Get(key.ToArray());

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

    public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None) => throw ReadOnlyException();
    public IWriteBatch StartWriteBatch() => throw ReadOnlyException();
    public void Flush(bool onlyWal = false) => throw ReadOnlyException();
    public void Clear() => throw ReadOnlyException();
    public void Compact() => throw ReadOnlyException();

    public void Dispose() { _levelDb.Dispose(); }

    private static NotSupportedException ReadOnlyException() => new("Read-only LevelDB adapter.");
}
