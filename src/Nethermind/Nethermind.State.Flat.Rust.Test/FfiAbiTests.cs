// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Nethermind.State.Flat.Rust.Interop;
using NUnit.Framework;

namespace Nethermind.State.Flat.Rust.Test;

[Parallelizable(ParallelScope.All)]
public class FfiAbiTests
{
    [Test]
    public unsafe void Library_speaks_this_abi_with_the_same_struct_layouts()
    {
        RustNativeLibrary.Require();

        Dictionary<FfiStruct, int> expectedSizes = new()
        {
            [FfiStruct.StoreCfg] = sizeof(FdbStoreCfg),
            [FfiStruct.KvDesc] = sizeof(FdbKvDesc),
            [FfiStruct.RocksApi] = RocksApiTable.SizeOf((int)NativeMethods.fdb_rocks_api_symbol_count()),
            [FfiStruct.ManagedKvVTable] = sizeof(ManagedKvVTable),
        };
        uint[] expectedOffsets = [.. FieldOffsets<FdbStoreCfg>(), .. FieldOffsets<FdbKvDesc>(), .. FieldOffsets<ManagedKvVTable>()];
        uint* sizes = stackalloc uint[16];
        uint count = NativeMethods.fdb_struct_sizes(sizes, 16);
        uint* offsets = stackalloc uint[64];
        uint offsetCount = NativeMethods.fdb_struct_offsets(offsets, 64);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(NativeMethods.fdb_abi_version(), Is.EqualTo(FfiAbi.Version));
            Assert.That(count, Is.EqualTo(expectedSizes.Count));
            foreach ((FfiStruct type, int size) in expectedSizes)
            {
                Assert.That(sizes[(int)type], Is.EqualTo(size), type.ToString());
            }
            Assert.That(new ReadOnlySpan<uint>(offsets, (int)offsetCount).ToArray(), Is.EqualTo(expectedOffsets));
        }
    }

    /// <summary>The offset of every field of <typeparamref name="T"/>, in declaration order.</summary>
    private static IEnumerable<uint> FieldOffsets<T>() where T : struct =>
        typeof(T).GetFields(BindingFlags.Instance | BindingFlags.Public).Select(static field => (uint)Marshal.OffsetOf<T>(field.Name));

    private static IEnumerable<TestCaseData> MapsCases()
    {
        const string nuget = "/root/.nuget/packages/nethermind.rocksdbbindings/runtimes/linux-x64/native/librocksdb.so";
        const string spaced = "/opt/my node/runtimes/linux-x64/native/librocksdb.so";
        static string Mapping(string path) => $"7f2c4a1c3000-7f2c4a1e5000 r-xp 00001000 103:02 1234567                    {path}";

        yield return new TestCaseData((object)new[] { Mapping(nuget), Mapping(nuget), Mapping("/usr/lib/libc.so.6"), "7ffd6a9e4000-7ffd6aa05000 rw-p 00000000 00:00 0                          [stack]" })
            .Returns(nuget).SetName("Segments of one library");
        yield return new TestCaseData((object)new[] { Mapping(spaced) }).Returns(spaced).SetName("Path with a space");
        yield return new TestCaseData((object)new[] { Mapping(nuget), Mapping(spaced) }).Returns(null).SetName("Two libraries");
        yield return new TestCaseData((object)new[] { Mapping("/usr/lib/libc.so.6") }).Returns(null).SetName("No library");
    }

    /// <returns>The library found, or null when the lookup refuses to pick one.</returns>
    [TestCaseSource(nameof(MapsCases))]
    public string? Finds_the_single_loaded_librocksdb(string[] maps)
    {
        try
        {
            return RocksApiTable.FindLoadedLibrary(maps);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
