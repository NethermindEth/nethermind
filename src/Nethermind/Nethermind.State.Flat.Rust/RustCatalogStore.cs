// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.RocksDbBindings;
using Nethermind.State.Flat.Rust.Interop;

namespace Nethermind.State.Flat.Rust;

/// <summary>
/// The catalog database of the native persisted-snapshot tier: the <see cref="DbNames.PersistedSnapshotCatalog"/>
/// <see cref="IDb"/> C# opened, which the tier writes as <c>SnapshotCatalog</c> does.
/// </summary>
/// <remarks>
/// A RocksDB catalog is driven by the native tier through the <see cref="RocksApiTable"/> on the very instance C#
/// opened, under a native lease held until disposal, which follows the native manager's destruction; its writes reach
/// the database's <c>TotalWrites</c> through <see cref="PublishWrites"/>, and its fatal errors its
/// <see cref="DbOnTheRocks.HandleFatalDbError"/>. Any other catalog is called back for every write, and counts its own.
/// </remarks>
internal sealed unsafe class RustCatalogStore : IDisposable
{
    private const int Succeeded = 0;

    private readonly IDb _db;
    private readonly DbOnTheRocks? _rocksDb;
    private readonly nint _defaultColumnFamily;
    private GCHandle _self;
    private int _disposed;

    private RustCatalogStore(IDb db, DbOnTheRocks? rocksDb, nint defaultColumnFamily)
    {
        _db = db;
        _rocksDb = rocksDb;
        _defaultColumnFamily = defaultColumnFamily;
        _self = GCHandle.Alloc(this);
    }

    /// <summary>
    /// Takes a native lease on a RocksDB <paramref name="catalogDb"/>, and a handle of its default column family,
    /// which <see cref="Dispose"/> releases.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The catalog database is being disposed.</exception>
    public static RustCatalogStore Create(IDb catalogDb)
    {
        if (catalogDb is not DbOnTheRocks rocksDb) return new RustCatalogStore(catalogDb, null, 0);

        rocksDb.AcquireNativeLease();
        try
        {
            return new RustCatalogStore(catalogDb, rocksDb, RocksApiTable.Shared.CreateDefaultColumnFamilyHandle(rocksDb._db.Handle));
        }
        catch
        {
            rocksDb.ReleaseNativeLease();
            throw;
        }
    }

    /// <summary>
    /// The catalog as the native tier reads it; <paramref name="callbacks"/> must stay valid until the native manager
    /// created with the descriptor returns, as it copies them.
    /// </summary>
    public FdbCatalogDesc Descriptor(ManagedCatalogVTable* callbacks)
    {
        if (_rocksDb is null)
        {
            *callbacks = new ManagedCatalogVTable
            {
                Ctx = GCHandle.ToIntPtr(_self),
                Put = (nint)(delegate* unmanaged<nint, byte*, nuint, byte*, nuint, int>)&Put,
                KeyExists = (nint)(delegate* unmanaged<nint, byte*, nuint, byte*, int>)&KeyExists,
                Remove = (nint)(delegate* unmanaged<nint, byte*, nuint, int>)&Remove,
            };
            return new FdbCatalogDesc { Kind = FdbCatalogKind.Managed, Managed = callbacks };
        }

        return new FdbCatalogDesc
        {
            Kind = FdbCatalogKind.Rocks,
            VerifyChecksums = _rocksDb.VerifyChecksum ? (byte)1 : (byte)0,
            WalSync = _rocksDb.WriteOptions!.GetSync() ? (byte)1 : (byte)0,
            RocksApi = RocksApiTable.Shared.Pointer,
            RocksDb = _rocksDb._db.Handle,
            DefaultColumnFamily = _defaultColumnFamily,
        };
    }

    /// <summary>
    /// Every row of the catalog, as <c>SnapshotCatalog.Load</c> reads them, packed for the native tier: a
    /// little-endian <c>uint</c> key length, the key, a little-endian <c>uint</c> value length and the value.
    /// </summary>
    public byte[] Rows()
    {
        using MemoryStream rows = new();
        Span<byte> length = stackalloc byte[sizeof(uint)];
        foreach (KeyValuePair<byte[], byte[]> row in _db.GetAll(ordered: false))
        {
            Write(rows, length, row.Key);
            Write(rows, length, row.Value ?? []);
        }

        return rows.ToArray();

        static void Write(MemoryStream rows, Span<byte> length, byte[] field)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)field.Length);
            rows.Write(length);
            rows.Write(field);
        }
    }

    /// <summary>Lets <see cref="DbOnTheRocks.HandleFatalDbError"/> of a RocksDB catalog shut the node down on an error the native tier hit.</summary>
    public void ReportFatal(string message) => _rocksDb?.HandleFatalDbError(new RocksDbException(message));

    /// <summary>Adds <paramref name="count"/> native catalog writes to the <c>TotalWrites</c> of a RocksDB catalog.</summary>
    public void PublishWrites(long count)
    {
        if (_rocksDb is null) return;

        for (long i = 0; i < count; i++) _rocksDb.UpdateWriteMetrics();
    }

    /// <summary>Releases the lease on the catalog once the native manager writing it is destroyed.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        if (_rocksDb is not null)
        {
            RocksApiTable.Shared.DestroyColumnFamilyHandle(_defaultColumnFamily);
            _rocksDb.ReleaseNativeLease();
        }

        if (_self.IsAllocated) _self.Free();
    }

    private static IDb Of(nint ctx) => ((RustCatalogStore)GCHandle.FromIntPtr(ctx).Target!)._db;

    [UnmanagedCallersOnly]
    private static int Put(nint ctx, byte* key, nuint keyLength, byte* value, nuint valueLength)
    {
        try
        {
            Of(ctx).PutSpan(new ReadOnlySpan<byte>(key, (int)keyLength), new ReadOnlySpan<byte>(value, (int)valueLength));
            return Succeeded;
        }
        catch (Exception exception)
        {
            return ManagedKvBridge.Fail(exception);
        }
    }

    [UnmanagedCallersOnly]
    private static int KeyExists(nint ctx, byte* key, nuint keyLength, byte* exists)
    {
        try
        {
            *exists = Of(ctx).KeyExists(new ReadOnlySpan<byte>(key, (int)keyLength)) ? (byte)1 : (byte)0;
            return Succeeded;
        }
        catch (Exception exception)
        {
            return ManagedKvBridge.Fail(exception);
        }
    }

    [UnmanagedCallersOnly]
    private static int Remove(nint ctx, byte* key, nuint keyLength)
    {
        try
        {
            Of(ctx).Remove(new ReadOnlySpan<byte>(key, (int)keyLength));
            return Succeeded;
        }
        catch (Exception exception)
        {
            return ManagedKvBridge.Fail(exception);
        }
    }
}
