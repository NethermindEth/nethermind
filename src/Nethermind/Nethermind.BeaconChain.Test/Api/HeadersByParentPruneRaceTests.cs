// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.Api;

public class HeadersByParentPruneRaceTests
{
    [Test]
    public async Task Headers_by_parent_root_is_404_when_the_parent_is_pruned_between_the_existence_check_and_the_children_read()
    {
        HookedColumnsDb db = new();
        await using BeaconApiTestHost host = await BeaconApiTestHost.StartAsync(BeaconChainSpec.Mainnet, db: db);
        Hash256 parent = BeaconApiTestHost.TestRoot(0x64);
        ulong slot = 412_500 * 32 + 7;
        host.Store.PutBlock(parent, BeaconApiTestHost.RichBlock(slot, BeaconApiTestHost.FilledHash(0x00)));
        host.Store.PutBlock(BeaconApiTestHost.TestRoot(0x65), BeaconApiTestHost.RichBlock(slot + 1, parent));
        db.BeforeNextIndexRead = () => host.Store.DeleteBlock(parent);

        HttpResponseMessage response = await host.GetAsync($"/eth/v1/beacon/headers?parent_root={parent}", "application/json");

        Assert.That(db.BeforeNextIndexRead, Is.Null, "the prune must have run between the two reads");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), "a block pruned mid-request is not retained; the pending entry a stored child keeps alive is not damage");
    }

    /// <summary>Runs a hook just before the next read of the block index, then behaves like the in-memory set.</summary>
    private sealed class HookedColumnsDb : IColumnsDb<BeaconChainDbColumns>
    {
        private readonly Dictionary<BeaconChainDbColumns, IDb> _columns = [];

        public Action? BeforeNextIndexRead { get; set; }

        public IDb GetColumnDb(BeaconChainDbColumns key)
        {
            if (!_columns.TryGetValue(key, out IDb? db))
            {
                _columns[key] = db = key == BeaconChainDbColumns.BlockIndex ? new HookedMemDb(this) : new MemDb();
            }

            return db;
        }

        public IEnumerable<BeaconChainDbColumns> ColumnKeys => Enum.GetValues<BeaconChainDbColumns>();
        public IColumnsWriteBatch<BeaconChainDbColumns> StartWriteBatch() => new InMemoryColumnWriteBatch<BeaconChainDbColumns>(this);
        public IColumnDbSnapshot<BeaconChainDbColumns> CreateSnapshot() => throw new NotSupportedException();
        public IDbMeta.DbMetric GatherMetric() => new();
        public void Flush(bool onlyWal = false) { }
        public void Dispose() { }

        private sealed class HookedMemDb(HookedColumnsDb owner) : MemDb
        {
            public override byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
            {
                Action? hook = owner.BeforeNextIndexRead;
                if (hook is not null && key.Length == 33 && key[0] == 0x01)
                {
                    owner.BeforeNextIndexRead = null;
                    hook();
                }

                return base.Get(key, flags);
            }
        }
    }
}
