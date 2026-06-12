// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Paprika.Crypto;
using Paprika.Data;

namespace Paprika.Store;

/// <summary>
/// Root page is a page that contains all the needed metadata from the point of view of the database.
/// It also includes the blockchain information like block hash or block number.
/// </summary>
/// <remarks>
/// Considerations for page types selected:
///
/// State: <see cref="Payload.StateRoot"/> 
/// Storage & Account Ids: <see cref="StorageFanOut.Level0"/>
/// </remarks>
public readonly unsafe struct RootPage(Page root) : IPage
{
    public ref PageHeader Header => ref root.Header;

    // Root page payload occupies exactly Page.PageSize - PageHeader.Size bytes and the resolver owns the page memory for the batch lifetime.
    public ref Payload Data => ref Unsafe.AsRef<Payload>(root.Payload);

    public void Assert(DbAddress address)
    {
        var nextFree = Data.NextFreePage;
        Debug.Assert(address < nextFree, $"Breached the next free page, NextFree: {nextFree}, retrieved {address}");
    }

    /// <summary>
    /// Represents the data of the page.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = Size)]
    public struct Payload
    {
        private const int Size = Page.PageSize - PageHeader.Size;

        /// <summary>
        /// The address of the next free page. This should be used rarely as pages should be reused
        /// with <see cref="AbandonedPage"/>.
        /// </summary>
        [FieldOffset(0)] public DbAddress NextFreePage;

        /// <summary>
        /// The account counter
        /// </summary>
        [FieldOffset(DbAddress.Size)] public uint AccountCounter;

        /// <summary>
        /// The first of the data pages.
        /// </summary>
        [FieldOffset(DbAddress.Size + sizeof(uint))]
        public DbAddress StateRoot;

        /// <summary>
        /// Metadata of this root
        /// </summary>
        [FieldOffset(DbAddress.Size * 2 + sizeof(uint))]
        public Metadata Metadata;

        /// <summary>
        /// Storage.
        /// </summary>
        [FieldOffset(DbAddress.Size * 2 + sizeof(uint) + Metadata.Size)]
        public DbAddressList.Of1024 StorageFanOut;

        public const int AbandonedStart =
            DbAddress.Size * 2 + sizeof(uint) + Metadata.Size + DbAddressList.Of1024.Size;

        /// <summary>
        /// The start of the abandoned pages.
        /// </summary>
        [FieldOffset(AbandonedStart)] public AbandonedList AbandonedList;

        public DbAddress GetNextFreePage()
        {
            var free = NextFreePage;
            NextFreePage = NextFreePage.Next;
            return free;
        }
    }

    public void Accept(IPageVisitor visitor, IPageResolver resolver)
    {
        var stateRoot = Data.StateRoot;
        if (stateRoot.IsNull == false)
        {
            var builder = new NibblePath.Builder(stackalloc byte[NibblePath.Builder.DecentSize]);

            new StateRootPage(resolver.GetAt(stateRoot)).Accept(ref builder, visitor, resolver, stateRoot);

            builder.Dispose();
        }

        StorageFanOut.Level0 storage = new(ref Data.StorageFanOut);
        storage.Accept(visitor, resolver);
        Data.AbandonedList.Accept(visitor, resolver);
    }

    /// <summary>
    /// How many id entries should be cached per readonly batch.
    /// </summary>
    public const int IdCacheLimit = 2_000;

    public bool TryGet(scoped in Key key, IReadOnlyBatchContext batch, out ReadOnlySpan<byte> result)
    {
        if (key.IsState)
        {
            if (Data.StateRoot.IsNull)
            {
                result = default;
                return false;
            }

            return new StateRootPage(batch.GetAt(Data.StateRoot)).TryGet(batch, key.Path, out result);
        }

        var cache = batch.IdCache;
        var keccak = key.Path.UnsafeAsKeccak;

        StorageFanOut.Level0 storage = new(ref Data.StorageFanOut);

        if (cache.TryGetValue(keccak, out var id))
        {
            if (id.IsNull)
            {
                result = default;
                return false;
            }
        }
        else
        {
            if (storage.TryGetId(keccak, out id, batch))
            {
                if (cache.Count < IdCacheLimit)
                {
                    cache[keccak] = id;
                }
            }
            else
            {
                // Not found, for now, not remember misses, remember miss
                // cache[keccak] = 0;
                result = default;
                return false;
            }
        }

        return storage.TryGetStorage(id, key.StoragePath, out result, batch);
    }

    public void SetRaw(in Key key, IBatchContext batch, ReadOnlySpan<byte> rawData)
    {
        if (key.IsState)
        {
            SetAtRoot(batch, key.Path, rawData, ref Data.StateRoot);
        }
        else
        {
            var keccak = key.Path.UnsafeAsKeccak;

            StorageFanOut.Level0 storage = new(ref Data.StorageFanOut);

            if (batch.IdCache.TryGetValue(keccak, out var id) == false)
            {
                // try fetch existing first
                if (storage.TryGetId(keccak, out id, batch) == false)
                {
                    Data.AccountCounter++;

                    // memoize in cache
                    batch.IdCache[keccak] = id = new ContractId(Data.AccountCounter);

                    // update root
                    storage.SetId(keccak, id, batch);
                }
                else
                {
                    // memoize in cache
                    batch.IdCache[keccak] = id;
                }
            }

            storage.SetStorage(id, key.StoragePath, rawData, batch);
        }
    }

    public void Destroy(IBatchContext batch, in NibblePath account)
    {
        // GC for storage
        DeleteByPrefix(Key.Merkle(account), batch);

        var keccak = account.UnsafeAsKeccak;

        // Destroy the Id entry about it
        StorageFanOut.Level0 storage = new(ref Data.StorageFanOut);
        storage.SetId(keccak, ContractId.Null, batch);

        // Destroy the account entry
        SetAtRoot(batch, account, ReadOnlySpan<byte>.Empty, ref Data.StateRoot);

        // Remove the cached
        batch.IdCache.Remove(keccak);
    }

    public void DeleteByPrefix(in Key prefix, IBatchContext batch)
    {
        if (prefix.IsState)
        {
            var data = batch.TryGetPageAlloc(ref Data.StateRoot, PageType.StateRoot);
            var updated = new StateRootPage(data).DeleteByPrefix(prefix.Path, batch);
            Data.StateRoot = batch.GetAddress(updated);
        }
        else
        {
            var keccak = prefix.Path.UnsafeAsKeccak;

            StorageFanOut.Level0 storage = new(ref Data.StorageFanOut);

            if (batch.IdCache.TryGetValue(keccak, out var id) == false)
            {
                // Not in cache, try fetch from db
                if (storage.TryGetId(keccak, out id, batch) == false)
                {
                    // Has never been mapped, return.
                    return;
                }
            }

            storage.DeleteStorageByPrefix(id, prefix.StoragePath, batch);
        }
    }

    private static void SetAtRoot(IBatchContext batch, in NibblePath path, in ReadOnlySpan<byte> rawData,
        ref DbAddress root)
    {
        var data = batch.TryGetPageAlloc(ref root, PageType.StateRoot);
        var updated = new StateRootPage(data).Set(path, rawData, batch);
        root = batch.GetAddress(updated);
    }

}

[StructLayout(LayoutKind.Sequential, Pack = sizeof(byte), Size = Size)]
public struct Metadata
{
    public const int Size = BlockNumberSize + Keccak.Size;
    private const int BlockNumberSize = sizeof(uint);

    public readonly uint BlockNumber;
    public readonly Keccak StateHash;

    public Metadata(uint blockNumber, Keccak stateHash)
    {
        BlockNumber = blockNumber;
        StateHash = stateHash;
    }
}
