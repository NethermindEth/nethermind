// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using System.Text;
using Nethermind.State.Flat.Rust.Interop;
using NUnit.Framework;

namespace Nethermind.State.Flat.Rust.Test;

/// <summary>
/// The fault injection of the library's test-api build (native/crates/flatdb-ffi/src/faults.rs), which this project
/// runs on when it was built; the production library has no such export.
/// </summary>
internal static unsafe partial class NativeFaults
{
    /// <summary>Where a fault strikes, as <c>faults::Fault</c> numbers it.</summary>
    public enum Fault : uint
    {
        /// <summary>The next key-value batch write fails with a RocksDB error carrying the message.</summary>
        KvWrite,

        /// <summary>The next write-ahead log or memtable flush fails with a RocksDB error carrying the message.</summary>
        KvFlush,

        /// <summary>The next guarded call on the store panics.</summary>
        PanicInExport,

        /// <summary>The next persist of the store's manager panics before it opens its batch.</summary>
        PanicInPersist,

        /// <summary>The next compaction of the store's manager panics.</summary>
        PanicInCompaction,

        /// <summary>The next conversion into the persisted-snapshot tier panics before it builds its table.</summary>
        PanicInConvert,

        /// <summary>The next persisted compaction panics before it merges.</summary>
        PanicInPersistedCompaction,

        /// <summary>The next manager created over the store panics once it opened the snapshots of its tier.</summary>
        PanicInPersistedLoad,

        /// <summary>The next read a bundle or scope serves from the persisted-snapshot tier panics.</summary>
        PanicInTierRead,

        /// <summary>The next conversion fails with an I/O error carrying the message once its table is written, cancelling its writes.</summary>
        ArenaWriteError,

        /// <summary>The next write of a catalog row fails with a RocksDB error carrying the message.</summary>
        CatalogPutError,

        /// <summary>The next conversion stops once its files are durable, before its catalog row is written, as a crash there would.</summary>
        SkipCatalogAdd,

        /// <summary>The next converted snapshot is validated against its source with one account more, which its table lacks.</summary>
        CorruptConvertedContent,
    }

    private static readonly Lazy<bool> _isAvailable = new(static () =>
        NativeMethods.IsAvailable
        && NativeLibrary.TryLoad("nethermind_flatdb", typeof(NativeMethods).Assembly, null, out nint library)
        && NativeLibrary.TryGetExport(library, nameof(fdb_test_inject_fault), out _));

    /// <summary>Ignores the calling test unless the library was built with its test-api feature.</summary>
    public static void Require()
    {
        RustNativeLibrary.Require();
        if (!_isAvailable.Value) Assert.Ignore("libnethermind_flatdb.so was built without the test-api feature");
    }

    /// <summary>Arms <paramref name="fault"/> on <paramref name="store"/> for the next pass through its point.</summary>
    public static void Inject(RustFlatStore store, Fault fault, string message)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(message);
        fixed (byte* data = bytes)
        {
            StatusMap.ThrowIfFailed(fdb_test_inject_fault(store, (uint)fault, data, (nuint)bytes.Length));
        }
    }

    [LibraryImport("nethermind_flatdb")]
    private static partial FdbStatus fdb_test_inject_fault(RustFlatStore store, uint fault, byte* message, nuint length);
}
