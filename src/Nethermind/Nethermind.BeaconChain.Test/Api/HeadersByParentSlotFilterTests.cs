// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Test.Storage;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Db;
using Nethermind.Logging;
using NUnit.Framework;
using Snappier;

namespace Nethermind.BeaconChain.Test.Api;

public class HeadersByParentSlotFilterTests
{
    private const string Json = "application/json";

    private const ulong Slot = 412_500 * 32 + 7;

    private static readonly Hash256 Parent = BeaconApiTestHost.TestRoot(0x70);
    private static readonly Hash256 Kept = BeaconApiTestHost.TestRoot(0x71);
    private static readonly Hash256 Broken = BeaconApiTestHost.TestRoot(0x72);

    public enum BrokenRecord { Missing, SlotPrefixOnly, NotSnappy, TooShortForSlot, TruncatedAfterSlot, ClaimsFourGiB, ClaimsTwoGiB }

    private TestLogger _log = null!;
    private BeaconApiTestHost _host = null!;

    [SetUp]
    public async Task StartHost()
    {
        _log = new TestLogger();
        _host = await BeaconApiTestHost.StartAsync(BeaconChainSpec.Mainnet, logManager: new OneLoggerLogManager(new ILogger(_log)));
        _host.Store.PutBlock(Parent, BeaconApiTestHost.RichBlock(Slot, BeaconApiTestHost.FilledHash(0x00)));
        _host.Store.PutBlock(Kept, BeaconApiTestHost.RichBlock(Slot + 1, Parent));
        _host.Store.PutBlock(Broken, BeaconApiTestHost.RichBlock(Slot + 2, Parent));
    }

    [TearDown]
    public async Task StopHost() => await _host.DisposeAsync();

    [Test]
    public async Task A_child_outside_the_slot_filter_is_never_decoded()
    {
        BreakRecord(BrokenRecord.SlotPrefixOnly);

        List<string> roots = await ListedRootsAsync($"&slot={Slot + 1}");

        Assert.That(roots, Is.EqualTo(new[] { Kept.ToString() }));
        Assert.That(_log.LogList.Where(entry => entry.Contains(Broken.ToString())), Is.Empty,
            "a decode attempt on the filtered-out child's bodyless record is the only thing that logs its root");
    }

    [Test]
    public async Task An_indexed_child_the_store_cannot_serve_is_left_out_rather_than_failing_the_list(
        [Values] BrokenRecord record, [Values] bool filterToBrokenSlot)
    {
        BreakRecord(record);

        List<string> roots = await ListedRootsAsync(filterToBrokenSlot ? $"&slot={Slot + 2}" : "");

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(roots, Does.Not.Contain(Broken.ToString()));
        Assert.That(roots.Contains(Kept.ToString()), Is.EqualTo(!filterToBrokenSlot));
        Assert.That(_log.LogList.Any(entry => entry.Contains(Broken.ToString())), Is.EqualTo(record != BrokenRecord.Missing));
    }

    [Test]
    public async Task A_child_with_a_valid_slot_and_a_random_body_is_left_out([Range(0, 15)] int seed)
    {
        Random random = new(seed);
        byte[] ssz = new byte[BeaconApiTestHost.SlotPrefixOnlyBlockSsz(0).Length + random.Next(1, 4096)];
        random.NextBytes(ssz);
        BeaconApiTestHost.SlotPrefixOnlyBlockSsz(Slot + 2).CopyTo(ssz, 0);
        _host.Db.GetColumnDb(BeaconChainDbColumns.Blocks).Set(Broken.Bytes, Snappy.CompressToArray(ssz));

        Assert.That(await ListedRootsAsync(""), Is.EqualTo(new[] { Kept.ToString() }));
    }

    [Test]
    public async Task A_parent_stored_before_the_index_lists_all_its_children_once_the_database_is_upgraded()
    {
        Hash256 legacyParent = BeaconApiTestHost.TestRoot(0x80);
        Hash256 legacyChild = BeaconApiTestHost.TestRoot(0x81);
        Hash256 laterChild = BeaconApiTestHost.TestRoot(0x82);
        _host.WriteLegacyBlock(legacyParent, BeaconApiTestHost.RichBlock(Slot + 10, BeaconApiTestHost.FilledHash(0x00)));
        _host.WriteLegacyBlock(legacyChild, BeaconApiTestHost.RichBlock(Slot + 11, legacyParent));
        _host.Store.PutBlock(laterChild, BeaconApiTestHost.RichBlock(Slot + 12, legacyParent));
        _host.Store.SetSchemaVersion(1);
        _host.Store.EnsureSchemaVersion();

        HttpResponseMessage response = await _host.GetAsync($"/eth/v1/beacon/headers?parent_root={legacyParent}", Json);
        string raw = await BeaconApiTestHost.ReadSuccessfulBodyAsync(response);
        Assert.That(JsonDocument.Parse(raw).RootElement.GetProperty("data").EnumerateArray().Select(entry => entry.GetProperty("root").GetString()),
            Is.EquivalentTo(new[] { legacyChild.ToString(), laterChild.ToString() }), "after the rebuild the index covers every stored block, so the list is complete");
    }

    private void BreakRecord(BrokenRecord record)
    {
        IDb blocks = _host.Db.GetColumnDb(BeaconChainDbColumns.Blocks);
        switch (record)
        {
            case BrokenRecord.Missing:
                blocks.Remove(Broken.Bytes);
                break;
            case BrokenRecord.SlotPrefixOnly:
                blocks.Set(Broken.Bytes, Snappy.CompressToArray(BeaconApiTestHost.SlotPrefixOnlyBlockSsz(Slot + 2)));
                break;
            case BrokenRecord.NotSnappy:
                blocks.Set(Broken.Bytes, BeaconChainStoreBlockSlotTests.NotSnappy);
                break;
            case BrokenRecord.TooShortForSlot:
                blocks.Set(Broken.Bytes, Snappy.CompressToArray(BeaconApiTestHost.SlotPrefixOnlyBlockSsz(Slot + 2).AsSpan(0, 50)));
                break;
            case BrokenRecord.TruncatedAfterSlot:
                byte[] whole = blocks.Get(Broken.Bytes)!;
                blocks.PutSpan(Broken.Bytes, whole.AsSpan(0, whole.Length / 2));
                break;
            case BrokenRecord.ClaimsFourGiB:
                blocks.Set(Broken.Bytes, BeaconChainStoreBlockSlotTests.ClaimsFourGiB);
                break;
            default:
                blocks.Set(Broken.Bytes, BeaconChainStoreBlockSlotTests.ClaimsTwoGiB);
                break;
        }
    }

    private async Task<List<string>> ListedRootsAsync(string slotQuery)
    {
        HttpResponseMessage response = await _host.GetAsync($"/eth/v1/beacon/headers?parent_root={Parent}{slotQuery}", Json);
        string raw = await BeaconApiTestHost.ReadSuccessfulBodyAsync(response);
        return JsonDocument.Parse(raw).RootElement.GetProperty("data").EnumerateArray()
            .Select(entry => entry.GetProperty("root").GetString()!)
            .ToList();
    }
}
