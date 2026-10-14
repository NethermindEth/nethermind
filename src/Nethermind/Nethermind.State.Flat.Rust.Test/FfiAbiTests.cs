// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
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
            [FfiStruct.StoreInfo] = sizeof(FdbStoreInfo),
            [FfiStruct.Stats] = sizeof(FdbStats),
            [FfiStruct.StateId] = sizeof(FfiStateId),
            [FfiStruct.Account] = sizeof(FfiAccount),
            [FfiStruct.MgrCfg] = sizeof(FdbMgrCfg),
            [FfiStruct.Host] = sizeof(FdbHost),
            [FfiStruct.Counters] = sizeof(FdbCounters),
            [FfiStruct.Observation] = sizeof(FdbObservation),
            [FfiStruct.PersistedNode] = sizeof(FfiPersistedNode),
            [FfiStruct.PersistedEvent] = sizeof(FdbPersistedEvent),
            [FfiStruct.RobInfo] = sizeof(FfiRobInfo),
            [FfiStruct.ScopeFlags] = sizeof(FfiScopeFlags),
            [FfiStruct.NodeRecord] = sizeof(FfiNodeRecord),
            [FfiStruct.TierCfg] = sizeof(FdbTierCfg),
            [FfiStruct.CatalogDesc] = sizeof(FdbCatalogDesc),
            [FfiStruct.ManagedCatalogVTable] = sizeof(ManagedCatalogVTable),
            [FfiStruct.TierGaugeRow] = sizeof(FfiTierGaugeRow),
        };
        uint[] expectedOffsets =
        [
            .. FieldOffsets<FdbStoreCfg>(), .. FieldOffsets<FdbKvDesc>(), .. FieldOffsets<ManagedKvVTable>(), .. FieldOffsets<FdbStoreInfo>(),
            .. FieldOffsets<FdbStats>(), .. FieldOffsets<FfiStateId>(), .. FieldOffsets<FfiAccount>(),
            .. FieldOffsets<FdbMgrCfg>(), .. FieldOffsets<FdbHost>(), .. FieldOffsets<FdbCounters>(), .. FieldOffsets<FdbObservation>(),
            .. FieldOffsets<FfiPersistedNode>(), .. FieldOffsets<FdbPersistedEvent>(), .. FieldOffsets<FfiRobInfo>(),
            .. FieldOffsets<FfiScopeFlags>(), .. FieldOffsets<FfiNodeRecord>(), .. FieldOffsets<FdbTierCfg>(),
            .. FieldOffsets<FdbCatalogDesc>(), .. FieldOffsets<ManagedCatalogVTable>(), .. FieldOffsets<FfiTierGaugeRow>(),
        ];
        uint* sizes = stackalloc uint[32];
        uint count = NativeMethods.fdb_struct_sizes(sizes, 32);
        uint* offsets = stackalloc uint[256];
        uint offsetCount = NativeMethods.fdb_struct_offsets(offsets, 256);

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

    /// <summary>A failed native call throws what the C# backend throws for the same failure.</summary>
    [TestCase((int)FdbStatus.GatherTimeout, typeof(InvalidOperationException))]
    [TestCase((int)FdbStatus.WrongBaseState, typeof(InvalidOperationException))]
    [TestCase((int)FdbStatus.Cancelled, typeof(OperationCanceledException))]
    [TestCase((int)FdbStatus.ShutDown, typeof(ObjectDisposedException))]
    [TestCase((int)FdbStatus.HashMismatch, typeof(NodeHashMismatchException))]
    [TestCase((int)FdbStatus.Corrupt, typeof(InvalidDataException))]
    [TestCase((int)FdbStatus.Io, typeof(IOException))]
    [TestCase((int)FdbStatus.ValidationFailed, typeof(InvalidOperationException))]
    public void Statuses_throw_the_exceptions_of_the_csharp_backend(int status, Type expected) =>
        Assert.That(StatusMap.ToException((FdbStatus)status, "failed"), Is.TypeOf(expected));

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
