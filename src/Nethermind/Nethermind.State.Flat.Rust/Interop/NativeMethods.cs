// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: DisableRuntimeMarshalling]

namespace Nethermind.State.Flat.Rust.Interop;

/// <summary>Exports of <c>libnethermind_flatdb</c>, built from <c>native/</c> (see native/crates/flatdb-ffi/src/lib.rs).</summary>
/// <remarks>
/// Handles C# owns are passed as their <see cref="SafeHandle"/>, so a call keeps them alive, except a read-only bundle
/// or scope, passed as the pointer of a <see cref="BundleBaseHandle.Call"/> lease, which does; reads copy into caller
/// buffers and report the length, -1 for an absent value, or <see cref="FdbStatus.BufferTooSmall"/> with the length
/// needed.
/// </remarks>
internal static unsafe partial class NativeMethods
{
    private const string LibraryName = "nethermind_flatdb";

    private static readonly Lazy<string?> _unavailableReason = new(FindUnavailableReason);

    /// <summary>Whether the native library loads and speaks the ABI of this adapter.</summary>
    public static bool IsAvailable => _unavailableReason.Value is null;

    /// <summary>Why the native library cannot be used, or null when it can.</summary>
    public static string? UnavailableReason => _unavailableReason.Value;

    /// <exception cref="InvalidOperationException">The native library is missing or speaks another ABI.</exception>
    public static void EnsureAvailable()
    {
        if (_unavailableReason.Value is { } reason) throw new InvalidOperationException(reason);
    }

    private static string? FindUnavailableReason()
    {
        if (!NativeLibrary.TryLoad(LibraryName, typeof(NativeMethods).Assembly, null, out nint library))
        {
            return $"The Rust flat DB backend needs lib{LibraryName}.so next to the executable. Build it with " +
                "`dotnet build -p:BuildRustFlat=true`, or set FlatDb.Backend=CSharp.";
        }

        if (!NativeLibrary.TryGetExport(library, nameof(fdb_abi_version), out _))
        {
            return $"The lib{LibraryName}.so found is not the Rust flat DB library; rebuild it.";
        }

        uint version = fdb_abi_version();
        return version == FfiAbi.Version
            ? null
            : $"lib{LibraryName}.so speaks ABI version {version} but this build expects {FfiAbi.Version}; rebuild it.";
    }

    [LibraryImport(LibraryName)]
    public static partial uint fdb_abi_version();

    [LibraryImport(LibraryName)]
    public static partial uint fdb_struct_sizes(uint* sizes, uint capacity);

    [LibraryImport(LibraryName)]
    public static partial uint fdb_struct_offsets(uint* offsets, uint capacity);

    [LibraryImport(LibraryName)]
    public static partial nuint fdb_last_error(byte* buffer, nuint capacity);

    [LibraryImport(LibraryName)]
    public static partial uint fdb_rocks_api_symbol_count();

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_rocks_api_symbol(uint index, byte** name, nuint* nameLength);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_store_create(FdbStoreCfg* cfg, FdbKvDesc* kv, nint* store, FdbStoreInfo* info);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_store_destroy(nint store);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_store_take_stats(RustFlatStore store, FdbStats* stats);

    /// <summary><c>fdb_store_take_stats</c> of a store being released, whose handle is already closed.</summary>
    [LibraryImport(LibraryName, EntryPoint = "fdb_store_take_stats")]
    public static partial FdbStatus fdb_released_store_take_stats(nint store, FdbStats* stats);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_store_log_take(RustFlatStore store, NativeLogSource* source, NativeLogLevel* level, byte* buffer, nuint capacity, nuint* length, byte* has);

    /// <summary><c>fdb_store_log_take</c> of the records logged outside any store.</summary>
    [LibraryImport(LibraryName, EntryPoint = "fdb_store_log_take")]
    public static partial FdbStatus fdb_unrouted_log_take(nint noStore, NativeLogSource* source, NativeLogLevel* level, byte* buffer, nuint capacity, nuint* length, byte* has);

    [LibraryImport(LibraryName)]
    public static partial ulong fdb_store_log_dropped(RustFlatStore store);

    /// <summary><c>fdb_store_log_dropped</c> of the records logged outside any store.</summary>
    [LibraryImport(LibraryName, EntryPoint = "fdb_store_log_dropped")]
    public static partial ulong fdb_unrouted_log_dropped(nint noStore);

    [LibraryImport(LibraryName)]
    public static partial nuint fdb_seam_counters(ulong* counts, ulong* nanoseconds, nuint capacity);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_seam_counters_by_thread(ulong* osThreadIds, ulong* counts, ulong* nanoseconds, uint threadCapacity, uint counterCapacity, uint* threads);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_seam_counter_name(uint index, byte** name, nuint* nameLength);

    [LibraryImport(LibraryName)]
    public static partial void fdb_seam_timing(byte enabled);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_store_flush(RustFlatStore store);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_store_clear(RustFlatStore store);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_reader_open(RustFlatStore store, uint flags, byte detailedMetrics, nint* reader, FfiStateId* currentState);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_reader_release(nint reader);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_reader_is_preimage_mode(ReaderHandle reader, byte* preimage);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_reader_get_account(ReaderHandle reader, byte* address, FfiAccount* account, byte* found);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_reader_get_slot(ReaderHandle reader, byte* address, ulong* slot, ulong* value, byte* found);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_reader_load_state_rlp(ReaderHandle reader, byte* path, byte pathLength, uint readFlags, byte* buffer, nuint capacity, long* length);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_reader_load_storage_rlp(ReaderHandle reader, byte* addressHash, byte* path, byte pathLength, uint readFlags, byte* buffer, nuint capacity, long* length);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_reader_get_account_raw(ReaderHandle reader, byte* addressHash, byte* buffer, nuint capacity, long* length);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_reader_get_storage_raw(ReaderHandle reader, byte* addressHash, byte* slotHash, ulong* value, byte* found);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_reader_account_iter(ReaderHandle reader, byte* startKey, byte* endKey, nint* iterator);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_reader_storage_iter(ReaderHandle reader, byte* accountKey, byte* startSlotKey, byte* endSlotKey, nint* iterator);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_iter_next(IteratorHandle iterator, byte* key, byte** value, nuint* valueLength, byte* hasEntry);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_iter_release(nint iterator);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_batch_open(RustFlatStore store, FfiStateId* from, FfiStateId* to, uint writeFlags, nint* batch, FfiStateId* currentState);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_batch_apply(BatchHandle batch, byte* ops, nuint length);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_batch_commit(BatchHandle batch);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_batch_release(nint batch);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_mgr_create(RustFlatStore store, FdbMgrCfg* cfg, FdbHost* host, nint* manager);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_signal_exit(ManagerHandle manager);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_shutdown(ManagerHandle manager);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_destroy(nint manager);

    [LibraryImport(LibraryName)]
    public static partial FdbCounters* fdb_stats(nint manager);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_drain_observations(nint manager, FdbObservation* buffer, nuint capacity, nuint* count);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_take_persisted_event(ManagerHandle manager, FdbPersistedEvent* persistedEvent);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_persisted_event_release(nint owner);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_queue_compaction(ManagerHandle manager, FfiStateId* to, long* stallNs);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_has_state(ManagerHandle manager, FfiStateId* stateId, byte* has);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_persisted_state(ManagerHandle manager, FfiStateId* persisted);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_reset_persisted(ManagerHandle manager);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_add_to_persistence(ManagerHandle manager, FfiStateId* latest);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_flush_to_persistence(ManagerHandle manager, FfiStateId* persisted);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_drop_unreachable(ManagerHandle manager, FfiStateId* head);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_take_fatal_error(ManagerHandle manager);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_clear_bundle_cache(ManagerHandle manager);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_rob_gather(ManagerHandle manager, FfiStateId* baseBlock, uint readerFlags, nint* rob, FfiRobInfo* info);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_rob_release(nint rob);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_rob_get_account(nint rob, byte* address, FfiAccount* account, byte* found, ReadSource* source);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_rob_self_destruct_idx(nint rob, byte* address, int* index);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_rob_get_slot(nint rob, byte* address, ulong* slot, int selfDestructIndex, byte filtered, ulong* value, byte* found, ReadSource* source);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_rob_load_state_rlp(nint rob, byte* path, byte pathLength, byte* hash, uint readFlags, byte* buffer, nuint capacity, long* length, ReadSource* source);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_rob_load_storage_rlp(nint rob, byte* addressHash, byte* path, byte pathLength, byte* hash, uint readFlags, byte* buffer, nuint capacity, long* length, ReadSource* source);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_scope_open(ManagerHandle manager, FfiStateId* baseBlock, uint readerFlags, FfiScopeFlags* flags, nint* scope, FfiRobInfo* info);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_scope_create(nint rob, nint* own, uint ownCount, FfiScopeFlags* flags, nint* scope, FfiRobInfo* info);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_scope_release(nint scope);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_scope_get_account(nint scope, byte* address, byte promote, FfiAccount* account, byte* found, ReadSource* source);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_scope_promote_account(nint scope, byte* address, FfiAccount* account);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_scope_set_account(nint scope, byte* address, FfiAccount* account);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_scope_self_destruct_idx(nint scope, byte* address, int* index);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_scope_get_slot(nint scope, byte* address, ulong* slot, int selfDestructIndex, ulong* value, byte* found, ReadSource* source);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_scope_set_slot(nint scope, byte* address, ulong* slot, ulong* value);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_scope_clear_storage(nint scope, byte* address, byte* addressHash);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_scope_load_state_rlp(nint scope, byte* path, byte pathLength, byte* hash, uint readFlags, byte* buffer, nuint capacity, long* length, ReadSource* source);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_scope_load_storage_rlp(nint scope, byte* addressHash, byte* path, byte pathLength, byte* hash, uint readFlags, byte* buffer, nuint capacity, long* length, ReadSource* source);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_scope_set_state_node(nint scope, byte* path, byte pathLength, byte* hash, byte* rlp, uint rlpLength);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_scope_set_storage_node(nint scope, byte* addressHash, byte* path, byte pathLength, byte* hash, byte* rlp, uint rlpLength);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_scope_put_state_nodes(nint scope, FfiNodeRecord* records, uint count, byte* blob, nuint blobLength);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_scope_put_storage_nodes(nint scope, byte* addressHash, FfiNodeRecord* records, uint count, byte* blob, nuint blobLength);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_scope_commit(nint scope, FfiStateId* from, FfiStateId* to, byte keep, nint* snapshot);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_snapshot_release(nint snapshot);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_add_snapshot(ManagerHandle manager, SnapshotHandle snapshot);
}
