// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: DisableRuntimeMarshalling]

namespace Nethermind.State.Flat.Rust.Interop;

/// <summary>Exports of <c>libnethermind_flatdb</c>, built from <c>native/</c> (see native/crates/flatdb-ffi/src/lib.rs).</summary>
internal static unsafe partial class NativeMethods
{
    private const string LibraryName = "nethermind_flatdb";

    private static readonly Lazy<string?> _unavailableReason = new(FindUnavailableReason);

    /// <summary>Whether the native library loads and speaks the ABI of this adapter.</summary>
    public static bool IsAvailable => _unavailableReason.Value is null;

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
    public static partial FdbStatus fdb_store_create(FdbStoreCfg* cfg, FdbKvDesc* kv, nint* store);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_store_destroy(nint store);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_store_take_read_count(nint store, ulong* count);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_debug_put(nint store, uint column, byte* key, nuint keyLength, byte* value, nuint valueLength);

    [LibraryImport(LibraryName)]
    public static partial FdbStatus fdb_debug_get(nint store, uint column, byte* key, nuint keyLength, byte* buffer, nuint capacity, long* valueLength);
}
