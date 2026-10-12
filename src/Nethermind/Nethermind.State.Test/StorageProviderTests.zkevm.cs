// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Reflection;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Store.Test;

public partial class StorageProviderTests
{
    [Test]
    public void Get_ManyReads_IndexesOriginalValuesEagerly()
    {
        using Context ctx = new(UseFlat, preBlockCaches: null);
        WorldState provider = BuildStorageProvider(ctx);
        provider.Commit(Frontier.Instance);
        for (int i = 1; i <= 2048; i++)
        {
            provider.Get(new StorageCell(ctx.Address1, (UInt256)i), out _);
        }

        OptimizedDictionary<StorageCell, UInt256> originals =
            (OptimizedDictionary<StorageCell, UInt256>)GetPrivateField(provider._persistentStorageProvider, "_originalValues");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(originals.Count, Is.EqualTo(2048), "Guest reads must index every original value immediately.");
            Assert.That(typeof(PersistentStorageProvider).GetField("_pendingOriginalValues", BindingFlags.Instance | BindingFlags.NonPublic), Is.Null,
                "The guest must not allocate a deferred capture buffer.");
        }
        provider.Commit(Frontier.Instance);
        Assert.That(originals.Count, Is.Zero, "Commit must clear the guest original-value index.");
    }
}
