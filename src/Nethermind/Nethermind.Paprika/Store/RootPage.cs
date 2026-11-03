using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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
        private DbAddressList.Of1024 StorageFanOut;

        [UnscopedRef]
        public StorageFanOut.Level0 Storage => new(ref StorageFanOut);

        internal const int FormatStart =
            DbAddress.Size * 2 + sizeof(uint) + Metadata.Size + DbAddressList.Of1024.Size;

        /// <summary>
        /// Identifies the store's format and seals this root.
        /// </summary>
        [FieldOffset(FormatStart)] public RootFormat Format;

        private const int ReclaimStart = FormatStart + RootFormat.Size;

        /// <summary>
        /// The pages of the state dropped by <see cref="IBatch.Clear"/> that are not reused yet.
        /// </summary>
        [FieldOffset(ReclaimStart)] public ReclaimRange Reclaim;

        public const int AbandonedStart = ReclaimStart + ReclaimRange.Size;

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

        /// <summary>
        /// Drops the whole state and hands its pages to <see cref="Reclaim"/>. The account counter is kept, so contract ids are
        /// never reused.
        /// </summary>
        public void Clear(uint batchId, byte historyDepth)
        {
            StateRoot = DbAddress.Null;
            StorageFanOut = default;
            Metadata = default;
            AbandonedList = default;
            Reclaim = new ReclaimRange { AfterBatchId = batchId, Next = DbAddress.Page(historyDepth), End = NextFreePage };
        }
    }

    /// <summary>
    /// Seals the root, so that <see cref="IsSealed"/> detects a torn write of it.
    /// </summary>
    public void Seal() => Data.Format.Checksum = ComputeChecksum();

    /// <summary>
    /// Whether the root is unchanged since it was <see cref="Seal">sealed</see>.
    /// </summary>
    public bool IsSealed => Data.Format.Checksum == ComputeChecksum();

    /// <summary>
    /// The complement of CRC32C over the page's 64-bit words, with <see cref="RootFormat.Checksum"/> read as zero.
    /// </summary>
    private uint ComputeChecksum()
    {
        const int checksumAt = PageHeader.Size + Payload.FormatStart + RootFormat.ChecksumOffset;
        const int checksumWord = checksumAt / sizeof(ulong);
        const ulong checksumMask = ~((ulong)uint.MaxValue << (checksumAt % sizeof(ulong) * 8));

        var words = MemoryMarshal.Cast<byte, ulong>(root.Span);
        var crc = uint.MaxValue;
        for (var i = 0; i < words.Length; i++)
        {
            crc = BitOperations.Crc32C(crc, i == checksumWord ? words[i] & checksumMask : words[i]);
        }

        return ~crc;
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

        Data.Storage.Accept(visitor, resolver);
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

        ThrowIfNotKeccak(key.Path);

        if (TryGetContractId(key.Path.UnsafeAsKeccak, batch, out var id) == false)
        {
            result = default;
            return false;
        }

        return Data.Storage.TryGetStorage(id, key.StoragePath, out result, batch);
    }

    internal bool TryGetContractId(in Keccak keccak, IReadOnlyBatchContext batch, out ContractId id)
    {
        var cache = batch.IdCache;

        if (cache.TryGetValue(keccak, out id))
        {
            return id.IsNull == false;
        }

        if (Data.Storage.TryGetId(keccak, out id, batch))
        {
            if (cache.Count < IdCacheLimit)
            {
                cache[keccak] = id;
            }

            return true;
        }

        // Not found, for now, not remember misses, remember miss
        // cache[keccak] = 0;
        return false;
    }

    public void SetRaw(in Key key, IBatchContext batch, ReadOnlySpan<byte> rawData)
    {
        // Deleting an absent key or a tombstone must neither allocate a ContractId nor copy the path.
        if (rawData.IsEmpty && (!TryGet(key, batch, out var existing) || existing.IsEmpty))
        {
            return;
        }

        if (key.IsState)
        {
            SetAtRoot(batch, key.Path, rawData, ref Data.StateRoot);
        }
        else
        {
            ThrowIfNotKeccak(key.Path);

            var keccak = key.Path.UnsafeAsKeccak;

            if (batch.IdCache.TryGetValue(keccak, out var id) == false)
            {
                // try fetch existing first
                if (Data.Storage.TryGetId(keccak, out id, batch) == false)
                {
                    Data.AccountCounter++;

                    // memoize in cache
                    batch.IdCache[keccak] = id = new ContractId(Data.AccountCounter);

                    // update root
                    Data.Storage.SetId(keccak, id, batch);
                }
                else
                {
                    // memoize in cache
                    batch.IdCache[keccak] = id;
                }
            }

            Data.Storage.SetStorage(id, key.StoragePath, rawData, batch);
        }
    }

    public void Destroy(IBatchContext batch, in NibblePath account) =>
        throw new NotSupportedException(
            "Destroy maps the account to ContractId 0, which every destroyed account would then share. " +
            "Delete the storage with DeleteByPrefix and the account with an empty SetRaw instead.");

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
            ThrowIfNotKeccak(prefix.Path);

            var keccak = prefix.Path.UnsafeAsKeccak;

            if (batch.IdCache.TryGetValue(keccak, out var id) == false)
            {
                // Not in cache, try fetch from db
                if (Data.Storage.TryGetId(keccak, out id, batch) == false)
                {
                    // Has never been mapped, return.
                    return;
                }
            }

            Data.Storage.DeleteStorageByPrefix(id, prefix.StoragePath, batch);
        }
    }

    private static void SetAtRoot(IBatchContext batch, in NibblePath path, in ReadOnlySpan<byte> rawData,
        ref DbAddress root)
    {
        var data = batch.TryGetPageAlloc(ref root, PageType.StateRoot);
        var updated = new StateRootPage(data).Set(path, rawData, batch);
        root = batch.GetAddress(updated);
    }

    /// <summary>
    /// <see cref="NibblePath.UnsafeAsKeccak"/> reads 32 bytes from the start of the path, so the account path of a storage key
    /// must be a full, even-aligned Keccak.
    /// </summary>
    private static void ThrowIfNotKeccak(in NibblePath account)
    {
        if (account.Length != NibblePath.KeccakNibbleCount || account.IsOdd)
        {
            ThrowNotKeccak();
        }

        [DoesNotReturn]
        [StackTraceHidden]
        static void ThrowNotKeccak() =>
            throw new ArgumentException("The account path of a storage key must be a full, even-aligned 32-byte Keccak.");
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

/// <summary>
/// Identifies a root of this store's format and holds the root's checksum, see <see cref="RootPage.Seal"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = sizeof(byte), Size = Size)]
public struct RootFormat
{
    public const int Size = ChecksumOffset + sizeof(uint);
    public const int ChecksumOffset = sizeof(uint) + sizeof(ushort) + sizeof(byte) * 2;

    /// <summary>
    /// "PPRK" read as a little-endian <see cref="uint"/>.
    /// </summary>
    public const uint PaprikaMagic = 0x4B525050;

    public const ushort CurrentVersion = 1;

    public uint Magic;
    public ushort Version;

    /// <summary>
    /// The history depth the store was created with: the root of batch <c>b</c> is at page <c>b % HistoryDepth</c>.
    /// </summary>
    public byte HistoryDepth;

    public byte Reserved;
    public uint Checksum;

    public static RootFormat Create(byte historyDepth) =>
        new() { Magic = PaprikaMagic, Version = CurrentVersion, HistoryDepth = historyDepth };

    public readonly void ThrowIfIncompatible(byte historyDepth)
    {
        if (Magic != PaprikaMagic || Version != CurrentVersion)
        {
            throw new InvalidOperationException(
                $"The file is not a store of this format: magic 0x{Magic:X8} version {Version}, expected magic 0x{PaprikaMagic:X8} version {CurrentVersion}.");
        }

        if (HistoryDepth != historyDepth)
        {
            throw new InvalidOperationException(
                $"The store was created with history depth {HistoryDepth} and cannot be opened with history depth {historyDepth}.");
        }
    }
}

/// <summary>
/// The pages <c>[Next, End)</c> of a state that <see cref="IBatch.Clear"/> dropped in batch <see cref="AfterBatchId"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = sizeof(byte), Size = Size)]
public struct ReclaimRange
{
    public const int Size = sizeof(uint) + DbAddress.Size * 2;

    public uint AfterBatchId;
    public DbAddress Next;
    public DbAddress End;

    /// <summary>
    /// Takes the next page once no batch that can reach it is left, by the same rule as <see cref="AbandonedList.TryGet"/>.
    /// </summary>
    public bool TryTake(uint minBatchId, out DbAddress page)
    {
        if (minBatchId > 2 && AfterBatchId < minBatchId && Next < End)
        {
            page = Next;
            Next = Next.Next;
            return true;
        }

        page = default;
        return false;
    }
}
