// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Store.Test;

[TestFixture]
public class PrewarmerCodePrefetchTests
{
    private static readonly byte[] Code = [0x60, 0x01, 0x60, 0x00, 0x55, 0x00];
    private static readonly ValueHash256 CodeHash = ValueKeccak.Compute(Code);
    private static readonly Account Contract = new(1, 100, Keccak.EmptyTreeHash, new Hash256(CodeHash));

    [Test]
    public void Code_the_access_list_names_is_read_once_ahead_of_execution_for_every_reader_of_the_block(
        [Values] bool parentReaderTakes, [Values] bool accountCached)
    {
        (TrieStoreScopeProvider provider, TestMemDb codeKv, BlockHeader parent) = CommitContract();
        ManualResetEventSlim readAhead = SignalReads(codeKv);
        PreBlockCaches caches = PrepareCaches(parent);
        // An account carried over from the previous block is never read, but its code still is not cached.
        if (accountCached)
        {
            AddressAsKey key = TestItem.AddressA;
            caches.StateCache.Set(in key, Contract);
        }

        PrewarmerScopeProvider consumer = new(provider, new PrewarmerState(caches, isPrewarmer: false), LimboLogs.Instance, new StaticCodeCache(16), prefetchCode: true);

        IWorldStateScopeProvider.IScope scope = consumer.BeginScope(parent);
        scope.HintBal(AccessList()).Wait();
        Assert.That(readAhead.Wait(TimeSpan.FromSeconds(10)), "the code was never read ahead");

        // Parallel execution reads the pre-block state through parent readers that share the block's caches.
        PrewarmerScopeProvider parentReaders = new(provider, new PrewarmerState(caches, isPrewarmer: true), LimboLogs.Instance);
        using (IWorldStateScopeProvider.IScope reader = parentReaderTakes ? parentReaders.BeginScope(parent) : null)
        {
            Assert.That((reader ?? scope).CodeDb.GetCode(in CodeHash).ToArray(), Is.EqualTo(Code));
        }

        codeKv.KeyWasRead(CodeHash.ToByteArray(), 1);

        scope.Dispose();
        Assert.That(caches.CodePrefetcher, Is.Null, "code read ahead for one block must not stay held after it");
    }

    [TestCase(true, true, TestName = "Code the code cache holds is not read ahead, as execution needs no read for it")]
    [TestCase(false, false, TestName = "Code is not read ahead unless enabled, as parallel execution reads it as fast on demand")]
    public void Code_is_not_read_ahead(bool codeCached, bool prefetchCode)
    {
        (TrieStoreScopeProvider provider, TestMemDb codeKv, BlockHeader parent) = CommitContract();
        PreBlockCaches caches = PrepareCaches(parent);
        StaticCodeCache codeCache = new(16);
        if (codeCached) codeCache.Set(in CodeHash, new CodeInfo(Code));
        PrewarmerScopeProvider consumer = new(provider, new PrewarmerState(caches, isPrewarmer: false), LimboLogs.Instance, codeCache, prefetchCode);

        ManualResetEventSlim readAhead = SignalReads(codeKv);

        using IWorldStateScopeProvider.IScope scope = consumer.BeginScope(parent);
        scope.HintBal(AccessList()).Wait();

        if (!prefetchCode) Assert.That(caches.CodePrefetcher, Is.Null, "no prefetcher is started");
        // Readers are idle, so a queued read would start at once.
        Assert.That(readAhead.Wait(TimeSpan.FromMilliseconds(500)), Is.False);
        codeKv.KeyWasRead(CodeHash.ToByteArray(), 0);
    }
    /// <summary>Serves the contract's code and signals each read, as code is read ahead on a background reader.</summary>
    private static ManualResetEventSlim SignalReads(TestMemDb codeKv)
    {
        ManualResetEventSlim read = new();
        codeKv.ReadFunc = _ =>
        {
            read.Set();
            return Code;
        };
        return read;
    }

    private static ReadOnlyBlockAccessList AccessList() =>
        Build.A.BlockAccessList.WithAccountChanges(Build.An.AccountChanges.WithAddress(TestItem.AddressA).TestObject).TestObject;

    private static PreBlockCaches PrepareCaches(BlockHeader parent)
    {
        PreBlockCaches caches = new(TestPreBlockCachesConfig.Small);
        caches.PrepareFor(parent.StateRoot);
        return caches;
    }

    private static (TrieStoreScopeProvider Provider, TestMemDb CodeKv, BlockHeader Parent) CommitContract()
    {
        TestMemDb codeKv = new();
        TrieStoreScopeProvider provider = new(new TestRawTrieStore(new TestMemDb()), codeKv, UnavailableStateHeaderProvider.Instance, LimboLogs.Instance);
        using IWorldStateScopeProvider.IScope scope = provider.BeginScope(null);
        using (IWorldStateScopeProvider.ICodeSetter codeSetter = scope.CodeDb.BeginCodeWrite())
        {
            codeSetter.Set(in CodeHash, Code);
        }

        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
        {
            writeBatch.Set(TestItem.AddressA, Contract);
        }

        scope.Commit(1);
        return (provider, codeKv, Build.A.BlockHeader.WithStateRoot(scope.RootHash).WithNumber(1).TestObject);
    }
}
