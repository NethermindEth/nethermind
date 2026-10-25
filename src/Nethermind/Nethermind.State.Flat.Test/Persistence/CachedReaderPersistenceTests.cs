// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Persistence.TrieNodeLog;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Persistence;

[TestFixture]
public class CachedReaderPersistenceTests
{
    // The snap-sync wipe resets the persisted state pointer from a reader created right after Clear. A read that
    // leased a reader during the wipe used to leave it cached, so the reset read the pre-wipe (genesis) pointer back.
    [Test]
    public async Task Clear_ReaderLeasedDuringWipe_IsNotServedAfterIt()
    {
        StateId genesis = new(0, Keccak.Compute("genesis"));
        RocksDbPersistence db = new(new SnapshotableMemColumnsDb<FlatDbColumns>(), LimboLogs.Instance, NullTrieNodeLog.Instance);
        using (db.CreateWriteBatch(StateId.PreGenesis, genesis, WriteFlags.None)) { }

        IPersistence inner = Substitute.For<IPersistence>();
        inner.CreateReader(Arg.Any<ReaderFlags>()).Returns(call => db.CreateReader(call.Arg<ReaderFlags>()));

        using CancellationTokenSource cts = new();
        IProcessExitSource processExitSource = Substitute.For<IProcessExitSource>();
        processExitSource.Token.Returns(cts.Token);
        await using CachedReaderPersistence persistence = new(inner, processExitSource, LimboLogs.Instance);

        StateId leasedDuringWipe = default;
        inner.When(static p => p.Clear()).Do(_ =>
        {
            using (IPersistence.IPersistenceReader reader = persistence.CreateReader()) leasedDuringWipe = reader.CurrentState;
            db.Clear();
        });

        persistence.Clear();

        using IPersistence.IPersistenceReader afterWipe = persistence.CreateReader();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(leasedDuringWipe, Is.EqualTo(genesis));
            Assert.That(afterWipe.CurrentState, Is.EqualTo(StateId.PreGenesis));
        }
    }
}
