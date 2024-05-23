// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Snap;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Trie;
using NSubstitute;
using NUnit.Framework;
using System;
using System.Collections.Generic;

namespace Nethermind.Synchronization.Test.SnapSync;

[TestFixture]
public class SnapProviderTests
{

    [Test]
    public void AddAccountRange_AccountListIsEmpty_ThrowArgumentException()
    {
        MemDb db = new();
        IDbProvider dbProvider = new DbProvider();
        dbProvider.RegisterDb(DbNames.State, db);
        using ProgressTracker progressTracker = new(Substitute.For<IBlockTree>(), dbProvider.GetDb<IDb>(DbNames.State), LimboLogs.Instance);
        dbProvider.RegisterDb(DbNames.Code, new MemDb());
        SnapProvider sut = new(progressTracker, dbProvider.CodeDb, new NodeStorage(dbProvider.StateDb), LimboLogs.Instance);

        Assert.That(
            () => sut.AddAccountRange(
                0,
                Keccak.Zero,
                Keccak.Zero,
                Array.Empty<PathWithAccount>(),
                Array.Empty<byte[]>().AsReadOnly()), Throws.ArgumentException);
    }


    [Test]
    public void AddAccountRange_ResponseHasEmptyListOfAccountsAndOneProof_ReturnsExpiredRootHash()
    {
        MemDb db = new();
        IDbProvider dbProvider = new DbProvider();
        dbProvider.RegisterDb(DbNames.State, db);
        using ProgressTracker progressTracker = new(Substitute.For<IBlockTree>(), dbProvider.GetDb<IDb>(DbNames.State), LimboLogs.Instance);
        dbProvider.RegisterDb(DbNames.Code, new MemDb());
        AccountRange accountRange = new(Keccak.Zero, Keccak.Zero, Keccak.MaxValue);
        using AccountsAndProofs accountsAndProofs = new();
        accountsAndProofs.PathAndAccounts = new List<PathWithAccount>().ToPooledList();
        accountsAndProofs.Proofs = new List<byte[]> { new byte[] { 0x0 } }.ToPooledList();

        SnapProvider sut = new(progressTracker, dbProvider.CodeDb, new NodeStorage(dbProvider.StateDb), LimboLogs.Instance);

        sut.AddAccountRange(accountRange, accountsAndProofs).Should().Be(AddRangeResult.ExpiredRootHash);
    }

}
