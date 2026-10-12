// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: DisableRuntimeMarshalling]

namespace Nethermind.State.Flat.Rust.Interop;

/// <summary>Exports of <c>libnethermind_flatdb</c>, built from <c>native/</c> (see native/crates/flatdb-ffi/src/lib.rs).</summary>
/// <remarks>
/// Handles C# owns are passed as their <see cref="SafeHandle"/>, so a call keeps them alive; reads copy into caller
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
    public static partial FdbCounters* fdb_stats(ManagerHandle manager);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_drain_observations(ManagerHandle manager, FdbObservation* buffer, nuint capacity, nuint* count);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_take_persisted_event(ManagerHandle manager, FdbPersistedEvent* persistedEvent);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_persisted_event_release(nint owner);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_bld_new(FfiStateId* from, FfiStateId* to, nuint accounts, nuint storages, nuint stateNodes, nuint storageNodes, nint* builder);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_bld_append(nint builder, byte* records, nuint length);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_bld_absorb(nint into, nint from);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_bld_release(nint builder);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_add_snapshot(ManagerHandle manager, nint builder);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_queue_compaction(ManagerHandle manager, FfiStateId* to, long* stallNs);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_gather(ManagerHandle manager, FfiStateId* baseBlock, uint readerFlags, byte filtered, nint* reader, FfiStateId* readerState);

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
    public static partial FdbStatus fdb_log_install(uint level);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_log_take(uint* level, byte* buffer, nuint capacity, nuint* length, byte* has);

    [LibraryImport(LibraryName)]
    public static partial ulong fdb_log_dropped();
}
