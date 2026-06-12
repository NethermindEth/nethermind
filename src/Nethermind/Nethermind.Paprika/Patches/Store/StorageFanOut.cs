// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Paprika.Crypto;
using Paprika.Data;

namespace Paprika.Store;

/// <summary>
/// This class is responsible for proving a good fan out for both:
/// 1. <see cref="Keccak"/> -> <see cref="uint"/> mapping needed to map contracts to their identifiers
/// 2. <see cref="uint"/> identifiers to <see cref="Level3Page"/> that provide the top of the tree for a given contract
///
/// This is done in two different ways. For identifiers, the biggest possible fan out is used
/// consuming <see cref="Level0.IdConsumedNibbles"/> from the keccak and moving it to the upper bits of the <see cref="uint"/>.
/// This distributes hashes of addresses to 64k buckets.
///
/// For storage, the identifier is kept as low as possible (counting from 0), meaning,
/// that the highest bits will be used only for large networks. This makes it small on upper levels but ensures
/// that on lower levels will distribute nicely. It still ensures a good fan out that does not blow up with the number of pages occupied.
/// </summary>
public static class StorageFanOut
{
    public const int LevelCount = 2;

    public const string ScopeIds = "Ids";
    public const string ScopeStorage = "Storage";

    public enum Type
    {
        /// <summary>
        /// Represents the mapping of Keccak->int
        /// </summary>
        Id,

        /// <summary>
        /// Represents the actual storage mapped NibblePath ->int
        /// </summary>
        Storage
    }

    public static (uint next, int index) GetIndex(uint at, int level)
    {
        const int length = DbAddressList.Of1024.Count;
        const int lengthMask = length - 1;
        var lengthBits = BitOperations.Log2(length);

        // Always extract from top bits to low, so for level 0 it will be (21, 31] etc

        const int bitsInUint = sizeof(uint) * 8;
        var shift = bitsInUint - (level + 1) * lengthBits;

        var index = (int)((at >> shift) & lengthMask);

        Debug.Assert(0 <= index && index < length);

        var nextMask = (1U << shift) - 1;
        var next = nextMask & at;

        return (next, index);
    }

    /// <summary>
    /// Provides a convenient data structure for <see cref="RootPage"/>,
    /// to hold a list of child addresses of <see cref="DbAddressList.IDbAddressList"/> but with addition of
    /// handling the updates to addresses.
    /// </summary>
    public readonly ref struct Level0(ref DbAddressList.Of1024 addresses)
    {
        private const int Level = 0;
        private readonly ref DbAddressList.Of1024 _addresses = ref addresses;

        private bool TryGet(IPageResolver batch, uint at, scoped in NibblePath key, Type type,
            out ReadOnlySpan<byte> result)
        {
            var (next, index) = GetIndex(at, Level);

            var addr = _addresses[index];
            if (addr.IsNull)
            {
                result = default;
                return false;
            }

            return Level1Page.Wrap(batch.GetAt(addr))
                .TryGet(batch, next, key, type, out result);
        }

        private void Set(IBatchContext batch, uint at, in NibblePath key, Type type, in ReadOnlySpan<byte> data)
        {
            var (next, index) = GetIndex(at, Level);
            var addr = _addresses[index];

            if (addr.IsNull)
            {
                batch.GetNewCleanPage<Level1Page>(out addr).Set(next, key, type, data, batch);
                _addresses[index] = addr;
                return;
            }

            // The page exists, update
            var updated = Level1Page.Wrap(batch.GetAt(addr)).Set(next, key, type, data, batch);
            _addresses[index] = batch.GetAddress(updated);
        }

        public void Accept(IPageVisitor visitor, IPageResolver resolver)
        {
            using var scope = visitor.Scope(nameof(StorageFanOut));

            for (var i = 0; i < DbAddressList.Of1024.Count; i++)
            {
                var addr = _addresses[i];
                if (!addr.IsNull)
                {
                    Level1Page.Wrap(resolver.GetAt(addr)).Accept(visitor, resolver, addr);
                }
            }
        }

        public bool TryGetId(in Keccak keccak, out ContractId id, IPageResolver batch)
        {
            Unsafe.SkipInit(out id);

            var at = BuildIdIndex(NibblePath.FromKey(keccak), out var sliced);

            if (TryGet(batch, at, sliced, Type.Id, out var result))
            {
                id = new ContractId(BinaryPrimitives.ReadUInt32LittleEndian(result));
                return true;
            }

            return false;
        }

        public void SetId(Keccak keccak, ContractId id, IBatchContext batch)
        {
            var at = BuildIdIndex(NibblePath.FromKey(keccak), out var sliced);

            Span<byte> span = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(span, id.Value);

            Set(batch, at, sliced, Type.Id, span);
        }

        // Id encoding
        private const int IdConsumedNibbles = 4;
        private const int IdNibblesToShiftUp = NibblePath.NibblePerByte * sizeof(uint) - IdConsumedNibbles;
        private const int IdShift = IdNibblesToShiftUp * NibblePath.NibbleShift;

        private static uint BuildIdIndex(in NibblePath path, out NibblePath sliced)
        {
            // Combined 1024 at level0 + 64 at level 1 for 
            Debug.Assert(DbAddressList.Of1024.Length * DbAddressList.Of64.Length == 16 * 16 * 16 * 16,
                "Should combine properly");

            sliced = path.SliceFrom(IdConsumedNibbles);

            var combined = (path.Nibble0 << (NibblePath.NibbleShift * 3)) +
                           (path.GetAt(1) << (NibblePath.NibbleShift * 2)) +
                           (path.GetAt(2) << (NibblePath.NibbleShift * 1)) +
                           path.GetAt(IdConsumedNibbles - 1);

            return (uint)combined << IdShift;
        }

        /// <summary>
        /// A counterpart to <see cref="BuildIdIndex"/>.
        /// </summary>
        public static int NormalizeAtForId(uint at) => (int)(at >> IdShift);

        public bool TryGetStorage(ContractId id, scoped in NibblePath path, out ReadOnlySpan<byte> result,
            IReadOnlyBatchContext batch) =>
            TryGet(batch, id.Value, path, Type.Storage, out result);

        public void SetStorage(ContractId id, scoped in NibblePath path, ReadOnlySpan<byte> data, IBatchContext batch)
        {
            Set(batch, id.Value, path, Type.Storage, data);
        }

        public void DeleteStorageByPrefix(ContractId id, scoped in NibblePath prefix, IBatchContext batch)
        {
            var (next, index) = GetIndex(id.Value, Level);
            var addr = _addresses[index];

            if (addr.IsNull)
            {
                return;
            }

            // The page exists, update
            _addresses[index] =
                batch.GetAddress(Level1Page.Wrap(batch.GetAt(addr)).DeleteByPrefix(next, prefix, batch));
        }
    }

    /// <summary>
    /// Represents a fan out for:
    /// - ids with <see cref="DbAddressList.Of4"/>
    /// - storage with <see cref="DbAddressList.Of1024"/>
    /// </summary>
    /// <param name="page"></param>
    [method: DebuggerStepThrough]
    public readonly unsafe struct Level1Page(Page page) : IPage<Level1Page>
    {
        private const int Level = 1;

        // Level1Page is a Page wrapper with no extra fields; the resolver keeps the underlying page memory alive for the batch operation.
        public static Level1Page Wrap(Page page) => Unsafe.As<Page, Level1Page>(ref page);
        public static PageType DefaultType => PageType.FanOutPage;

        private ref PageHeader Header => ref page.Header;

        // Payload is explicitly sized to the available page payload bytes and is only accessed while the resolver-owned page is alive.
        private ref Payload Data => ref Unsafe.AsRef<Payload>(page.Payload);

        public void Clear()
        {
            Data.Ids.Clear();
            Data.Storage.Clear();
        }

        public bool IsClean => Data.Ids.IsClean & Data.Storage.IsClean;

        public bool TryGet(IPageResolver batch, uint at, scoped in NibblePath key, Type type,
            out ReadOnlySpan<byte> result)
        {
            DbAddress addr;

            if (type == Type.Id)
            {
                addr = Data.Ids[Level0.NormalizeAtForId(at)];
                if (addr.IsNull)
                {
                    result = default;
                    return false;
                }

                var p = batch.GetAt(addr);
                return p.Header.PageType == PageType.Bottom
                    ? new BottomPage(p).TryGet(batch, key, out result)
                    : new DataPage(p).TryGet(batch, key, out result);
            }

            Debug.Assert(type == Type.Storage);

            var (next, index) = GetIndex(at, Level);
            addr = Data.Storage[index];

            if (addr.IsNull)
            {
                result = default;
                return false;
            }

            return Level2Page.Wrap(batch.GetAt(addr)).TryGet(batch, next, key, out result);
        }

        public Page Set(uint at, in NibblePath key, Type type, in ReadOnlySpan<byte> data, IBatchContext batch)
        {
            if (Header.BatchId != batch.BatchId)
            {
                // the page is from another batch, meaning, it's readonly. Copy
                var writable = batch.GetWritableCopy(page);
                return new Level1Page(writable).Set(at, key, type, data, batch);
            }

            DbAddress addr;

            if (type == Type.Id)
            {
                var normalized = Level0.NormalizeAtForId(at);
                addr = Data.Ids[normalized];

                var p = addr.IsNull
                    ? batch.GetNewCleanPage<BottomPage>(out addr).AsPage()
                    : batch.EnsureWritableCopy(ref addr);

                Data.Ids[normalized] = addr;

                p.Set(key, data, batch);

                return page;
            }

            Debug.Assert(type == Type.Storage);

            var (next, index) = GetIndex(at, Level);
            addr = Data.Storage[index];

            if (addr.IsNull)
            {
                batch.GetNewCleanPage<Level2Page>(out addr);
            }

            Data.Storage[index] = batch.GetAddress(Level2Page.Wrap(batch.GetAt(addr)).Set(next, key, data, batch));

            return page;
        }

        public Page DeleteByPrefix(uint at, in NibblePath prefix, IBatchContext batch)
        {
            if (Header.BatchId != batch.BatchId)
            {
                // the page is from another batch, meaning, it's readonly.
                var writable = batch.GetWritableCopy(page);
                return new Level1Page(writable).DeleteByPrefix(at, prefix, batch);
            }

            var (next, index) = GetIndex(at, Level);

            var addr = Data.Storage[index];

            if (addr.IsNull)
            {
                return page;
            }

            // update after set
            Data.Storage[index] =
                batch.GetAddress(Level2Page.Wrap(batch.GetAt(addr)).DeleteByPrefix(next, prefix, batch));

            return page;
        }

        public void Accept(IPageVisitor visitor, IPageResolver resolver, DbAddress addr)
        {
            var builder = new NibblePath.Builder(stackalloc byte[NibblePath.Builder.DecentSize]);

            using var scope = visitor.On(ref builder, this, addr);

            using (visitor.Scope(ScopeIds))
            {
                for (var i = 0; i < DbAddressList.Of64.Count; i++)
                {
                    var bucket = Data.Ids[i];

                    if (!bucket.IsNull)
                    {
                        DataPage.Wrap(resolver.GetAt(bucket)).Accept(ref builder, visitor, resolver, bucket);
                    }
                }
            }

            using (visitor.Scope(ScopeStorage))
            {
                for (var i = 0; i < DbAddressList.Of1024.Count; i++)
                {
                    var bucket = Data.Storage[i];
                    if (!bucket.IsNull)
                    {
                        Level2Page.Wrap(resolver.GetAt(bucket)).Accept(ref builder, visitor, resolver, bucket);
                    }
                }
            }

            builder.Dispose();
        }

        [StructLayout(LayoutKind.Explicit, Size = Size)]
        private struct Payload
        {
            private const int Size = Page.PageSize - PageHeader.Size;

            /// <summary>
            /// Ids are mapped using a single half-nibble
            /// </summary>
            [FieldOffset(0)] public DbAddressList.Of64 Ids;

            /// <summary>
            /// Storage is mapped further by another 2.5 nibble, making it 5 in total.
            /// </summary>
            [FieldOffset(DbAddressList.Of64.Size)] public DbAddressList.Of1024 Storage;
        }
    }

    [method: DebuggerStepThrough]
    public readonly unsafe struct Level2Page(Page page) : IPage<Level2Page>
    {
        private const int LocalKeySize = NibblePath.KeccakNibbleCount + 2;

        /// <summary>
        /// The local keys prepend 1 nibble. To make it easy to concatenate the oddity is set to 1,
        /// so that the keys are of form _N, where _ is a blank as it's an odd path, and N is a nibble.
        /// </summary>
        private const int PathOddity = 1;

        /// <summary>
        /// We set the start level to be aligned with the path oddity. This is required for
        /// the <see cref="DataPage"/> to properly fan out when it's big enough. The data page performs a fan out
        /// on even levels and assumes the aligned oddity. If we start on even level with odd path,
        /// it might result in some issues.
        /// </summary>
        private const int StartLevel = PathOddity;

        private static NibblePath BuildLocalKey(in NibblePath key, byte bucket, Span<byte> workingSet)
        {
            return NibblePath.Single(bucket, PathOddity).Append(key, workingSet);
        }

        public static (byte bucket, int index) GetIndex(uint at)
        {
            Debug.Assert(at < FanOutCount * 16);

            var bucket = (byte)(at & NibblePath.NibbleMask);
            var index = (int)(at >> NibblePath.NibbleShift);

            Debug.Assert(index < FanOutCount);
            Debug.Assert(bucket <= NibblePath.NibbleMask);

            return (bucket, index);
        }

        private const int FanOutCount = DbAddressList.Of256.Count;

        // Level2Page is a Page wrapper with no extra fields; the resolver keeps the underlying page memory alive for the batch operation.
        public static Level2Page Wrap(Page page) => Unsafe.As<Page, Level2Page>(ref page);
        public static PageType DefaultType => PageType.FanOutPage;

        public void Clear() => Data.Addresses.Clear();
        public bool IsClean => Data.Addresses.IsClean;

        private ref PageHeader Header => ref page.Header;

        // Payload is explicitly sized to the available page payload bytes and is only accessed while the resolver-owned page is alive.
        private ref Payload Data => ref Unsafe.AsRef<Payload>(page.Payload);


        public bool TryGet(IPageResolver batch, uint at, scoped in NibblePath key,
            out ReadOnlySpan<byte> result)
        {
            var (bucket, index) = GetIndex(at);

            var addr = Data.Addresses[index];
            if (addr.IsNull)
            {
                result = default;
                return false;
            }

            var localKey = BuildLocalKey(key, bucket, stackalloc byte[LocalKeySize]);
            var child = batch.GetAt(addr);

            // Page.TryGet returns page payload memory; localKey is only used to navigate to that payload.
#pragma warning disable CS9080
            return child.TryGet(batch, localKey, out result);
#pragma warning restore CS9080
        }

        public Page Set(uint at, in NibblePath key, in ReadOnlySpan<byte> data, IBatchContext batch)
        {
            if (Header.BatchId != batch.BatchId)
            {
                // the page is from another batch, meaning, it's readonly. Copy
                var writable = batch.GetWritableCopy(page);
                return new Level2Page(writable).Set(at, key, data, batch);
            }

            var (next, index) = GetIndex(at);
            var addr = Data.Addresses[index];

            var child = addr.IsNull
                ? batch.GetNewCleanPage<BottomPage>(out addr, StartLevel).AsPage()
                : batch.EnsureWritableCopy(ref addr);

            Data.Addresses[index] = addr;

            var localKey = BuildLocalKey(key, next, stackalloc byte[LocalKeySize]);

            Debug.Assert(batch.WasWritten(addr));

            child.Set(localKey, data, batch);

            return page;
        }

        public Page DeleteByPrefix(uint at, in NibblePath prefix, IBatchContext batch)
        {
            if (Header.BatchId != batch.BatchId)
            {
                // the page is from another batch, meaning, it's readonly. Copy
                var writable = batch.GetWritableCopy(page);
                return new Level2Page(writable).DeleteByPrefix(at, prefix, batch);
            }

            var (next, index) = GetIndex(at);
            var addr = Data.Addresses[index];

            if (addr.IsNull)
            {
                return page;
            }

            var child = addr.IsNull
                ? batch.GetNewCleanPage<BottomPage>(out addr).AsPage()
                : batch.EnsureWritableCopy(ref addr);
            Data.Addresses[index] = addr;

            var localKey = BuildLocalKey(prefix, next, stackalloc byte[LocalKeySize]);

            Debug.Assert(batch.WasWritten(addr));

            if (child.Header.PageType == PageType.Bottom)
                new BottomPage(child).DeleteByPrefix(localKey, batch);
            else
                new DataPage(child).DeleteByPrefix(localKey, batch);

            return page;
        }

        public void Accept(ref NibblePath.Builder builder, IPageVisitor visitor, IPageResolver resolver, DbAddress addr)
        {
            resolver.Prefetch(Data.Addresses);

            using var scope = visitor.On(ref builder, this, addr);

            for (var i = 0; i < FanOutCount; i++)
            {
                var bucket = Data.Addresses[i];

                if (!bucket.IsNull)
                {
                    var child = resolver.GetAt(bucket);
                    if (child.Header.PageType == PageType.Bottom)
                        new BottomPage(child).Accept(ref builder, visitor, resolver, bucket);
                    else
                        new DataPage(child).Accept(ref builder, visitor, resolver, bucket);
                }
            }
        }

        [StructLayout(LayoutKind.Explicit, Size = Size)]
        private struct Payload
        {
            private const int Size = Page.PageSize - PageHeader.Size;

            [FieldOffset(0)] public DbAddressList.Of256 Addresses;
        }
    }
}
