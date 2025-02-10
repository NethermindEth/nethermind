// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using Nethermind.TxPool.Collections;
using Nethermind.TxPool.Filters;
using NSubstitute;
using NUnit.Framework;
using Org.BouncyCastle.Pqc.Crypto.Lms;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.TxPool.Test;
internal class DelegatedAccountFilterTest
{
    [Test]
    public void Accept_SenderIsNotDelegated_ReturnsAccepted()
    {
        IChainHeadSpecProvider headInfoProvider = Substitute.For<IChainHeadSpecProvider>();
        headInfoProvider.GetCurrentHeadSpec().Returns(Prague.Instance);
        TxDistinctSortedPool standardPool = new TxDistinctSortedPool(MemoryAllowance.MemPoolSize, Substitute.For<IComparer<Transaction>>(), NullLogManager.Instance);
        TxDistinctSortedPool blobPool = new BlobTxDistinctSortedPool(10, Substitute.For<IComparer<Transaction>>(), NullLogManager.Instance);
        DelegatedAccountFilter filter = new(headInfoProvider, standardPool, blobPool, Substitute.For<IReadOnlyStateProvider>(), new CodeInfoRepository(), new DelegationCache());
        Transaction transaction = Build.A.Transaction.SignedAndResolved(new EthereumEcdsa(0), TestItem.PrivateKeyA).TestObject;
        TxFilteringState state = new();

        AcceptTxResult result = filter.Accept(transaction, ref state, TxHandlingOptions.None);

        Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
    }

    [Test]
    public void Accept_SenderIsDelegatedWithNoTransactionsInPool_ReturnsAccepted()
    {
        IDb stateDb = new MemDb();
        IDb codeDb = new MemDb();
        TrieStore trieStore = new(stateDb, LimboLogs.Instance);
        IWorldState stateProvider = new WorldState(trieStore, codeDb, LimboLogs.Instance);
        stateProvider.CreateAccount(TestItem.AddressA, 0);
        CodeInfoRepository codeInfoRepository = new();
        byte[] code = [.. Eip7702Constants.DelegationHeader, .. TestItem.PrivateKeyA.Address.Bytes];
        codeInfoRepository.InsertCode(stateProvider, code, TestItem.AddressA, Prague.Instance);
        IChainHeadSpecProvider headInfoProvider = Substitute.For<IChainHeadSpecProvider>();
        headInfoProvider.GetCurrentHeadSpec().Returns(Prague.Instance);
        TxDistinctSortedPool standardPool = new TxDistinctSortedPool(MemoryAllowance.MemPoolSize, Substitute.For<IComparer<Transaction>>(), NullLogManager.Instance);
        TxDistinctSortedPool blobPool = new BlobTxDistinctSortedPool(10, Substitute.For<IComparer<Transaction>>(), NullLogManager.Instance);
        DelegatedAccountFilter filter = new(headInfoProvider, standardPool, blobPool, stateProvider, codeInfoRepository, new DelegationCache());
        Transaction transaction = Build.A.Transaction.SignedAndResolved(new EthereumEcdsa(0), TestItem.PrivateKeyA).TestObject;
        TxFilteringState state = new(transaction, stateProvider);

        AcceptTxResult result = filter.Accept(transaction, ref state, TxHandlingOptions.None);

        Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
    }

    [Test]
    public void Accept_SenderIsDelegatedWithOneTransactionInPoolWithSameNonce_ReturnsAccepted()
    {
        IChainHeadSpecProvider headInfoProvider = Substitute.For<IChainHeadSpecProvider>();
        headInfoProvider.GetCurrentHeadSpec().Returns(Prague.Instance);
        TxDistinctSortedPool standardPool = new TxDistinctSortedPool(MemoryAllowance.MemPoolSize, Substitute.For<IComparer<Transaction>>(), NullLogManager.Instance);
        TxDistinctSortedPool blobPool = new BlobTxDistinctSortedPool(10, Substitute.For<IComparer<Transaction>>(), NullLogManager.Instance);
        Transaction inPool = Build.A.Transaction.SignedAndResolved(new EthereumEcdsa(0), TestItem.PrivateKeyA).TestObject;
        standardPool.TryInsert(inPool.Hash, inPool);
        IDb stateDb = new MemDb();
        IDb codeDb = new MemDb();
        TrieStore trieStore = new(stateDb, LimboLogs.Instance);
        IWorldState stateProvider = new WorldState(trieStore, codeDb, LimboLogs.Instance);
        stateProvider.CreateAccount(TestItem.AddressA, 0);
        CodeInfoRepository codeInfoRepository = new();
        byte[] code = [.. Eip7702Constants.DelegationHeader, .. TestItem.PrivateKeyA.Address.Bytes];
        codeInfoRepository.InsertCode(stateProvider, code, TestItem.AddressA, Prague.Instance);
        DelegatedAccountFilter filter = new(headInfoProvider, standardPool, blobPool, stateProvider, codeInfoRepository, new DelegationCache());
        Transaction transaction = Build.A.Transaction.SignedAndResolved(new EthereumEcdsa(0), TestItem.PrivateKeyA).TestObject;
        TxFilteringState state = new(transaction, stateProvider);

        AcceptTxResult result = filter.Accept(transaction, ref state, TxHandlingOptions.None);

        Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
    }

    [Test]
    public void Accept_SenderIsDelegatedWithOneTransactionInPoolWithDifferentNonce_ReturnsFutureNonceForDelegatedAccount()
    {
        IChainHeadSpecProvider headInfoProvider = Substitute.For<IChainHeadSpecProvider>();
        headInfoProvider.GetCurrentHeadSpec().Returns(Prague.Instance);
        TxDistinctSortedPool standardPool = new TxDistinctSortedPool(MemoryAllowance.MemPoolSize, Substitute.For<IComparer<Transaction>>(), NullLogManager.Instance);
        TxDistinctSortedPool blobPool = new BlobTxDistinctSortedPool(10, Substitute.For<IComparer<Transaction>>(), NullLogManager.Instance);
        Transaction inPool = Build.A.Transaction.SignedAndResolved(new EthereumEcdsa(0), TestItem.PrivateKeyA).TestObject;
        standardPool.TryInsert(inPool.Hash, inPool);
        IDb stateDb = new MemDb();
        IDb codeDb = new MemDb();
        TrieStore trieStore = new(stateDb, LimboLogs.Instance);
        IWorldState stateProvider = new WorldState(trieStore, codeDb, LimboLogs.Instance);
        stateProvider.CreateAccount(TestItem.AddressA, 0);
        CodeInfoRepository codeInfoRepository = new();
        byte[] code = [.. Eip7702Constants.DelegationHeader, .. TestItem.PrivateKeyA.Address.Bytes];
        codeInfoRepository.InsertCode(stateProvider, code, TestItem.AddressA, Prague.Instance);
        DelegatedAccountFilter filter = new(headInfoProvider, standardPool, blobPool, stateProvider, codeInfoRepository, new DelegationCache());
        Transaction transaction = Build.A.Transaction.WithNonce(1).SignedAndResolved(new EthereumEcdsa(0), TestItem.PrivateKeyA).TestObject;
        TxFilteringState state = new(transaction, stateProvider);

        AcceptTxResult result = filter.Accept(transaction, ref state, TxHandlingOptions.None);

        Assert.That(result, Is.EqualTo(AcceptTxResult.FutureNonceForDelegatedAccount));
    }

    private static object[] EipActiveCases =
    {
        new object[]{ true, AcceptTxResult.FutureNonceForDelegatedAccount },
        new object[]{ false, AcceptTxResult.Accepted},
    };
    [TestCaseSource(nameof(EipActiveCases))]
    public void Accept_Eip7702IsNotActivated_ReturnsExpected(bool isActive, AcceptTxResult expected)
    {
        IChainHeadSpecProvider headInfoProvider = Substitute.For<IChainHeadSpecProvider>();
        headInfoProvider.GetCurrentHeadSpec().Returns(isActive ? Prague.Instance : Cancun.Instance);
        TxDistinctSortedPool standardPool = new TxDistinctSortedPool(MemoryAllowance.MemPoolSize, Substitute.For<IComparer<Transaction>>(), NullLogManager.Instance);
        TxDistinctSortedPool blobPool = new BlobTxDistinctSortedPool(10, Substitute.For<IComparer<Transaction>>(), NullLogManager.Instance);
        Transaction inPool = Build.A.Transaction.WithNonce(0).SignedAndResolved(new EthereumEcdsa(0), TestItem.PrivateKeyA).TestObject;
        standardPool.TryInsert(inPool.Hash, inPool);
        IDb stateDb = new MemDb();
        IDb codeDb = new MemDb();
        TrieStore trieStore = new(stateDb, LimboLogs.Instance);
        IWorldState stateProvider = new WorldState(trieStore, codeDb, LimboLogs.Instance);
        stateProvider.CreateAccount(TestItem.AddressA, 0);
        CodeInfoRepository codeInfoRepository = new();
        byte[] code = [.. Eip7702Constants.DelegationHeader, .. TestItem.PrivateKeyA.Address.Bytes];
        codeInfoRepository.InsertCode(stateProvider, code, TestItem.AddressA, Prague.Instance);
        DelegatedAccountFilter filter = new(headInfoProvider, standardPool, blobPool, stateProvider, codeInfoRepository, new DelegationCache());
        Transaction transaction = Build.A.Transaction.WithNonce(1).SignedAndResolved(new EthereumEcdsa(0), TestItem.PrivateKeyA).TestObject;
        TxFilteringState state = new(transaction, stateProvider);

        AcceptTxResult result = filter.Accept(transaction, ref state, TxHandlingOptions.None);

        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public void Accept_SenderHasPendingDelegation_ReturnsPendingDelegation()
    {
        IChainHeadSpecProvider headInfoProvider = Substitute.For<IChainHeadSpecProvider>();
        headInfoProvider.GetCurrentHeadSpec().Returns(Prague.Instance);
        TxDistinctSortedPool standardPool = new TxDistinctSortedPool(MemoryAllowance.MemPoolSize, Substitute.For<IComparer<Transaction>>(), NullLogManager.Instance);
        TxDistinctSortedPool blobPool = new BlobTxDistinctSortedPool(10, Substitute.For<IComparer<Transaction>>(), NullLogManager.Instance);
        DelegationCache pendingDelegations = new();
        pendingDelegations.IncrementDelegationCount(TestItem.AddressA);
        DelegatedAccountFilter filter = new(headInfoProvider, standardPool, blobPool, Substitute.For<IReadOnlyStateProvider>(), new CodeInfoRepository(), pendingDelegations);
        Transaction transaction = Build.A.Transaction.WithNonce(0).SignedAndResolved(new EthereumEcdsa(0), TestItem.PrivateKeyA).TestObject;
        TxFilteringState state = new();

        AcceptTxResult result = filter.Accept(transaction, ref state, TxHandlingOptions.None);

        Assert.That(result, Is.EqualTo(AcceptTxResult.PendingDelegation));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Accept_AuthorityHasPendingTransaction_ReturnsDelegatorHasPendingTx(bool useBlobPool)
    {
        IChainHeadSpecProvider headInfoProvider = Substitute.For<IChainHeadSpecProvider>();
        headInfoProvider.GetCurrentHeadSpec().Returns(Prague.Instance);
        TxDistinctSortedPool standardPool = new TxDistinctSortedPool(MemoryAllowance.MemPoolSize, Substitute.For<IComparer<Transaction>>(), NullLogManager.Instance);
        TxDistinctSortedPool blobPool = new BlobTxDistinctSortedPool(10, Substitute.For<IComparer<Transaction>>(), NullLogManager.Instance);
        DelegatedAccountFilter filter = new(headInfoProvider, standardPool, blobPool, Substitute.For<IReadOnlyStateProvider>(), new CodeInfoRepository(), new());
        Transaction transaction;
        if (useBlobPool)
        {
            transaction
                = Build.A.Transaction
                .WithShardBlobTxTypeAndFields()
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            blobPool.TryInsert(transaction.Hash, transaction, out _);
        }
        else
        {
            transaction
                = Build.A.Transaction
                .WithNonce(0)
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            standardPool.TryInsert(transaction.Hash, transaction, out _);
        }
        TxFilteringState state = new();
        EthereumEcdsa ecdsa = new EthereumEcdsa(0);
        AuthorizationTuple authTuple = new AuthorizationTuple(0, TestItem.AddressB, 0, new Core.Crypto.Signature(0, 0, 27), TestItem.AddressA);
        Transaction setCodeTx = Build.A.Transaction
            .WithType(TxType.SetCode)
            .WithAuthorizationCode(authTuple)
            .SignedAndResolved(TestItem.PrivateKeyB)
            .TestObject;

        AcceptTxResult setCodeTxResult = filter.Accept(setCodeTx, ref state, TxHandlingOptions.None);

        Assert.That(setCodeTxResult, Is.EqualTo(AcceptTxResult.DelegatorHasPendingTx));
    }

    [Test]
    public void Accept_SetCodeTxHasAuthorityWithPendingTx_ReturnsDelegatorHasPendingTx()
    {
        IChainHeadSpecProvider headInfoProvider = Substitute.For<IChainHeadSpecProvider>();
        headInfoProvider.GetCurrentHeadSpec().Returns(Prague.Instance);
        TxDistinctSortedPool standardPool = new TxDistinctSortedPool(MemoryAllowance.MemPoolSize, Substitute.For<IComparer<Transaction>>(), NullLogManager.Instance);
        TxDistinctSortedPool blobPool = new BlobTxDistinctSortedPool(10, Substitute.For<IComparer<Transaction>>(), NullLogManager.Instance);
        DelegationCache pendingDelegations = new();
        pendingDelegations.IncrementDelegationCount(TestItem.AddressA);
        DelegatedAccountFilter filter = new(headInfoProvider, standardPool, blobPool, Substitute.For<IReadOnlyStateProvider>(), new CodeInfoRepository(), pendingDelegations);
        Transaction transaction = Build.A.Transaction
            .WithNonce(1)
            .SignedAndResolved(new EthereumEcdsa(0), TestItem.PrivateKeyA).TestObject;
        standardPool.TryInsert(transaction.Hash, transaction);
        Transaction setCodeTransaction = Build.A.Transaction
            .WithNonce(0)
            .WithType(TxType.SetCode)
            .WithMaxFeePerGas(9.GWei())
            .WithMaxPriorityFeePerGas(9.GWei())
                .WithGasLimit(100_000)
            .WithAuthorizationCode(new AuthorizationTuple(0, TestItem.AddressC, 0, new Core.Crypto.Signature(new byte[64], 0), TestItem.AddressA))
            .WithTo(TestItem.AddressB)
            .SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        TxFilteringState state = new();

        AcceptTxResult result = filter.Accept(setCodeTransaction, ref state, TxHandlingOptions.None);

        Assert.That(result, Is.EqualTo(AcceptTxResult.DelegatorHasPendingTx));
    }
}
