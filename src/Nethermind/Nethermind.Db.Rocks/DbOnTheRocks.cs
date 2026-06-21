// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Nethermind.Core;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;

namespace Nethermind.Db.Rocks;

/// <summary>
/// Nethermind key-value database backed by libmdbx.
/// </summary>
public partial class DbOnTheRocks : IDb, IMergeableKeyValueStore, ISortedKeyValueStore, IKeyValueStoreWithSnapshot, IReadOnlyNativeKeyValueStore
{
    private readonly ILogger _logger;
    private readonly IMergeOperator? _mergeOperator;
    private readonly uint _dbi;
    private bool _disposed;

    internal readonly MdbxEnvironment Mdbx;

    protected uint MainDbi => _dbi;

    public DbOnTheRocks(
        string basePath,
        DbSettings dbSettings,
        IDbConfig dbConfig,
        IRocksDbConfigFactory rocksDbConfigFactory,
        ILogManager logManager,
        IList<string>? columnFamilies = null,
        IntPtr? sharedCache = null)
    {
        Name = dbSettings.DbName;
        _mergeOperator = dbSettings.MergeOperator;
        _logger = logManager.GetClassLogger<DbOnTheRocks>();

        FullPath = GetFullDbPath(dbSettings.DbPath, basePath);
        if (dbSettings.DeleteOnStart && dbSettings.CanDeleteFolder && Directory.Exists(FullPath))
        {
            Directory.Delete(FullPath, recursive: true);
        }

        IRocksDbConfig rocksDbConfig = rocksDbConfigFactory.GetForDatabase(dbSettings.DbName, null);
        Mdbx = new MdbxEnvironment(FullPath, dbConfig, rocksDbConfig, _logger);
        _dbi = Mdbx.OpenTable(null);
    }

    protected DbOnTheRocks(
        string basePath,
        DbSettings dbSettings,
        IDbConfig dbConfig,
        IRocksDbConfigFactory rocksDbConfigFactory,
        ILogManager logManager,
        bool openMainTable,
        IntPtr? sharedCache = null)
    {
        Name = dbSettings.DbName;
        _mergeOperator = dbSettings.MergeOperator;
        _logger = logManager.GetClassLogger<DbOnTheRocks>();

        FullPath = GetFullDbPath(dbSettings.DbPath, basePath);
        if (dbSettings.DeleteOnStart && dbSettings.CanDeleteFolder && Directory.Exists(FullPath))
        {
            Directory.Delete(FullPath, recursive: true);
        }

        IRocksDbConfig rocksDbConfig = rocksDbConfigFactory.GetForDatabase(dbSettings.DbName, null);
        Mdbx = new MdbxEnvironment(FullPath, dbConfig, rocksDbConfig, _logger);
        _dbi = openMainTable ? Mdbx.OpenTable(null) : 0;
    }

    public string Name { get; }

    public string FullPath { get; }

    public byte[]? FirstKey => Mdbx.ExecuteRead(txn => MdbxCursorHelpers.GetEdge(txn, _dbi, MdbxCursorOp.First));

    public byte[]? LastKey => Mdbx.ExecuteRead(txn => MdbxCursorHelpers.GetEdge(txn, _dbi, MdbxCursorOp.Last));

    public KeyValuePair<byte[], byte[]?>[] this[byte[][] keys]
    {
        get
        {
            KeyValuePair<byte[], byte[]?>[] result = new KeyValuePair<byte[], byte[]?>[keys.Length];
            Mdbx.ExecuteRead(txn =>
            {
                for (int i = 0; i < keys.Length; i++)
                {
                    byte[] key = keys[i];
                    result[i] = new KeyValuePair<byte[], byte[]?>(key, Mdbx.Get(txn, _dbi, key));
                }
            });

            return result;
        }
    }

    public static string GetFullDbPath(string dbPath, string basePath) =>
        Path.IsPathRooted(dbPath) ? dbPath : Path.Combine(basePath, dbPath);

    public static string GetRocksDbVersion() => "libmdbx";

    public static IDictionary<string, string> ExtractOptions(string options)
    {
        Dictionary<string, string> parsed = new(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(options))
        {
            return parsed;
        }

        int index = 0;
        while (index < options.Length)
        {
            int separator = options.IndexOf('=', index);
            if (separator < 0)
            {
                break;
            }

            string key = options[index..separator].Trim();
            int valueStart = separator + 1;
            int valueEnd = FindOptionEnd(options, valueStart);
            if (key.Length > 0)
            {
                parsed[key] = options[valueStart..valueEnd].Trim();
            }

            index = valueEnd + 1;
        }

        return parsed;
    }

    public static string NormalizeRocksDbOptions(string rocksDbOptions)
    {
        if (string.IsNullOrWhiteSpace(rocksDbOptions))
        {
            return string.Empty;
        }

        IDictionary<string, string> parsed = ExtractOptions(rocksDbOptions);
        StringBuilder builder = new(rocksDbOptions.Length);
        foreach (KeyValuePair<string, string> option in parsed)
        {
            builder.Append(option.Key).Append('=').Append(option.Value).Append(';');
        }

        return builder.ToString();
    }

    public byte[]? Get(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) =>
        Mdbx.Get(_dbi, key);

    public Span<byte> GetSpan(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) =>
        Get(key, flags);

    public ReadOnlySpan<byte> GetNativeSlice(scoped ReadOnlySpan<byte> key, out IntPtr handle, ReadFlags flags = ReadFlags.None)
    {
        byte[]? data = Get(key, flags);
        if (data is null)
        {
            handle = IntPtr.Zero;
            return default;
        }

        handle = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, handle, data.Length);
        // The unmanaged copy is owned by the caller through DangerousReleaseHandle.
        unsafe
        {
            return new ReadOnlySpan<byte>((void*)handle, data.Length);
        }
    }

    public void DangerousReleaseHandle(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(handle);
        }
    }

    public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None) =>
        Mdbx.Put(_dbi, key, value);

    public void PutSpan(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None)
    {
        byte[] keyCopy = key.ToArray();
        byte[] valueCopy = value.ToArray();
        Mdbx.ExecuteWrite(txn => Mdbx.Put(txn, _dbi, keyCopy, valueCopy));
    }

    public void Merge(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None) =>
        Mdbx.Merge(_dbi, key, value, _mergeOperator);

    public IEnumerable<KeyValuePair<byte[], byte[]?>> GetAll(bool ordered = false) =>
        MdbxCursorHelpers.Enumerate(Mdbx, _dbi);

    public IEnumerable<byte[]> GetAllKeys(bool ordered = false)
    {
        foreach (KeyValuePair<byte[], byte[]?> item in GetAll(ordered))
        {
            yield return item.Key;
        }
    }

    public IEnumerable<byte[]> GetAllValues(bool ordered = false)
    {
        foreach (KeyValuePair<byte[], byte[]?> item in GetAll(ordered))
        {
            if (item.Value is not null)
            {
                yield return item.Value;
            }
        }
    }

    public IWriteBatch StartWriteBatch() =>
        new MdbxWriteBatch(Mdbx, _dbi, _mergeOperator);

    public ISortedView GetViewBetween(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive) =>
        new MdbxSortedView(Mdbx, _dbi, firstKeyInclusive, lastKeyExclusive);

    public IKeyValueStoreSnapshot CreateSnapshot() =>
        new MdbxKeyValueStoreSnapshot(Mdbx, _dbi);

    public IDb.DbMetric GatherMetric() =>
        new() { Size = Mdbx.GetDirectorySize() };

    public void Flush(bool onlyWal = false) =>
        Mdbx.Flush();

    public void Clear() =>
        Mdbx.DropTable(_dbi);

    public void Compact() =>
        Flush();

    public void SetWriteBuffer(long sizeBytes)
    {
    }

    public virtual void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Mdbx.Dispose();
        GC.SuppressFinalize(this);
    }

    protected IDb CreateColumnDb(string columnName, uint dbi, IMergeOperator? mergeOperator) =>
        new ColumnDb(this, columnName, dbi, mergeOperator);

    protected uint OpenColumn(string columnName) =>
        Mdbx.OpenTable(columnName);

    internal IWriteBatch CreateWriteBatch(uint dbi, IMergeOperator? mergeOperator) =>
        new MdbxWriteBatch(Mdbx, dbi, mergeOperator);

    internal IKeyValueStoreSnapshot CreateSnapshot(uint dbi) =>
        new MdbxKeyValueStoreSnapshot(Mdbx, dbi);

    private static int FindOptionEnd(string options, int valueStart)
    {
        int depth = 0;
        for (int i = valueStart; i < options.Length; i++)
        {
            char current = options[i];
            if (current == '{')
            {
                depth++;
            }
            else if (current == '}')
            {
                depth--;
            }
            else if (current == ';' && depth == 0)
            {
                return i;
            }
        }

        return options.Length;
    }
}
