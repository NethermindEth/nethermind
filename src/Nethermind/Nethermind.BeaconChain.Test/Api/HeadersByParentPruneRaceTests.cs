// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Net.Http;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;

namespace Nethermind.BeaconChain.Test.Api;

public class HeadersByParentPruneRaceTests
{
    [Test]
    public async Task Headers_by_parent_root_lists_the_remaining_children_when_the_parent_is_pruned_before_the_children_read()
    {
        HookedColumnsDb db = new();
        await using BeaconApiTestHost host = await BeaconApiTestHost.StartAsync(BeaconChainSpec.Mainnet, db: db);
        Hash256 parent = BeaconApiTestHost.TestRoot(0x64);
        ulong slot = 412_500 * 32 + 7;
        host.Store.PutBlock(parent, BeaconApiTestHost.RichBlock(slot, BeaconApiTestHost.FilledHash(0x00)));
        host.Store.PutBlock(BeaconApiTestHost.TestRoot(0x65), BeaconApiTestHost.RichBlock(slot + 1, parent));
        db.BeforeNextIndexRead = () => host.Store.DeleteBlock(parent);

        HttpResponseMessage response = await host.GetAsync($"/eth/v1/beacon/headers?parent_root={parent}", "application/json");

        Assert.That(db.BeforeNextIndexRead, Is.Null, "the prune must have run before the index read");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "a block pruned mid-request is not retained; the pending entry a stored child keeps alive is not damage");
        Assert.That((await BeaconApiTestHost.ReadJsonAsync(response)).RootElement.GetProperty("data").GetArrayLength(), Is.EqualTo(1), "the stored child is still listed");
    }

    [Test]
    public async Task Headers_by_parent_root_answers_an_empty_list_when_the_parent_is_imported_after_the_index_was_read()
    {
        HookedColumnsDb db = new();
        await using BeaconApiTestHost host = await BeaconApiTestHost.StartAsync(BeaconChainSpec.Mainnet, db: db);
        Hash256 parent = BeaconApiTestHost.TestRoot(0x66);
        db.AfterNextIndexRead = () => host.Store.PutBlock(parent, BeaconApiTestHost.RichBlock(412_500 * 32 + 7, BeaconApiTestHost.FilledHash(0x00)));

        HttpResponseMessage response = await host.GetAsync($"/eth/v1/beacon/headers?parent_root={parent}", "application/json");

        Assert.That(db.AfterNextIndexRead, Is.Null, "the import must have run after the index read");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "a parent imported mid-request is not a damaged index");
        Assert.That((await BeaconApiTestHost.ReadJsonAsync(response)).RootElement.GetProperty("data").GetArrayLength(), Is.EqualTo(0));
    }

    private sealed class HookedColumnsDb : TestColumnsDb
    {
        public Action? BeforeNextIndexRead { get; set; }
        public Action? AfterNextIndexRead { get; set; }

        protected override IDb CreateColumn(BeaconChainDbColumns key) => key == BeaconChainDbColumns.BlockIndex ? new HookedMemDb(this) : base.CreateColumn(key);

        private sealed class HookedMemDb(HookedColumnsDb owner) : MemDb
        {
            public override byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
            {
                bool indexKey = key.Length == 33 && key[0] == 0x01;
                Action? hook = owner.BeforeNextIndexRead;
                if (hook is not null && indexKey)
                {
                    owner.BeforeNextIndexRead = null;
                    hook();
                }

                byte[]? value = base.Get(key, flags);
                Action? after = owner.AfterNextIndexRead;
                if (after is not null && indexKey)
                {
                    owner.AfterNextIndexRead = null;
                    after();
                }

                return value;
            }
        }
    }
}
