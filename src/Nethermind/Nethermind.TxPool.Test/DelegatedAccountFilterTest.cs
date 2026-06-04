// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.Evm.State;
using Nethermind.TxPool.Collections;
using Nethermind.TxPool.Filters;
using NSubstitute;
using NUnit.Framework;
using System.Collections.Generic;
using Nethermind.Core.Test;

namespace Nethermind.TxPool.Test;

[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
internal class DelegatedAccountFilterTest
{
    private static readonly EthereumEcdsa Ecdsa = new(0);

    private static IChainHeadSpecProvider CreateHeadSpecProvider(IReleaseSpec spec)
    {
        IChainHeadSpecProvider provider = Substitute.For<IChainHeadSpecProvider>();
        provider.GetCurrentHeadSpec().Returns(spec);
        return provider;
    }

    private static (TxDistinctSortedPool standardPool, TxDistinctSortedPool blobPool) CreatePools()
    {
        TxDistinctSortedPool standardPool = new(new TxPoolConfig().Size, Substitute.For<IComparer<Transaction>>(), NullLogManager.Instance);
        TxDistinctSortedPool blobPool = new BlobTxDistinctSortedPool(10, Substitute.For<IComparer<Transaction>>(), NullLogManager.Instance);
        return (standardPool, blobPool);
    }

    private static DelegatedAccountFilter CreateFilter(
        IReleaseSpec spec,
        TxDistinctSortedPool standardPool,
        TxDistinctSortedPool blobPool,
        IReadOnlyStateProvider stateProvider = null,
        DelegationCache delegationCache = null)
    {
        IChainHeadSpecProvider headSpecProvider = CreateHeadSpecProvider(spec);
        return new DelegatedAccountFilter(
            headSpecProvider,
            standardPool,
            blobPool,
            stateProvider ?? Substitute.For<IReadOnlyStateProvider>(),
            delegationCache ?? new DelegationCache());
    }

    private static TestReadOnlyStateProvider CreateDelegatedStateProvider()
    {
        TestReadOnlyStateProvider stateProvider = new();
        stateProvider.CreateAccount(TestItem.AddressA, 0);
        byte[] code = [.. Eip7702Constants.DelegationHeader, .. TestItem.PrivateKeyA.Address.Bytes];
        stateProvider.InsertCode(code, TestItem.AddressA);
        return stateProvider;
    }

    [Test]
    public void Accept_SenderIsNotDelegated_ReturnsAccepted()
    {
        (TxDistinctSortedPool standardPool, TxDistinctSortedPool blobPool) = CreatePools();
        DelegatedAccountFilter filter = CreateFilter(Prague.Instance, standardPool, blobPool);
        Transaction transaction = Build.A.Transaction.SignedAndResolved(Ecdsa, TestItem.PrivateKeyA).TestObject;
        TxFilteringState state = new(transaction, Substitute.For<IAccountStateProvider>());

        AcceptTxResult result = filter.Accept(transaction, ref state, TxHandlingOptions.None);

        Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
    }

    [Test]
    public void Accept_SenderIsDelegatedWithNoTransactionsInPool_ReturnsAccepted()
    {
        TestReadOnlyStateProvider stateProvider = CreateDelegatedStateProvider();
        (TxDistinctSortedPool standardPool, TxDistinctSortedPool blobPool) = CreatePools();
        DelegatedAccountFilter filter = CreateFilter(Prague.Instance, standardPool, blobPool, stateProvider);
        Transaction transaction = Build.A.Transaction.SignedAndResolved(Ecdsa, TestItem.PrivateKeyA).TestObject;
        TxFilteringState state = new(transaction, stateProvider);

        AcceptTxResult result = filter.Accept(transaction, ref state, TxHandlingOptions.None);

        Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
    }

    private static readonly object[] DelegatedWithTxInPoolCases =
    {
        new object[] { 0, AcceptTxResult.Accepted },
        new object[] { 1, AcceptTxResult.NotCurrentNonceForDelegation },
    };

    [TestCaseSource(nameof(DelegatedWithTxInPoolCases))]
    public void Accept_SenderIsDelegatedWithOneTransactionInPool(int txNonce, AcceptTxResult expected)
    {
        (TxDistinctSortedPool standardPool, TxDistinctSortedPool blobPool) = CreatePools();
        Transaction inPool = Build.A.Transaction.SignedAndResolved(Ecdsa, TestItem.PrivateKeyA).TestObject;
        standardPool.TryInsert(inPool.Hash, inPool);
        TestReadOnlyStateProvider stateProvider = CreateDelegatedStateProvider();
        DelegatedAccountFilter filter = CreateFilter(Prague.Instance, standardPool, blobPool, stateProvider);
        Transaction transaction = Build.A.Transaction.WithNonce((UInt256)txNonce).SignedAndResolved(Ecdsa, TestItem.PrivateKeyA).TestObject;
        TxFilteringState state = new(transaction, stateProvider);

        AcceptTxResult result = filter.Accept(transaction, ref state, TxHandlingOptions.None);

        Assert.That(result, Is.EqualTo(expected));
    }

    private static readonly object[] Eip7702ActivationCases =
    {
        new object[] { true, AcceptTxResult.NotCurrentNonceForDelegation },
        new object[] { false, AcceptTxResult.Accepted },
    };

    [TestCaseSource(nameof(Eip7702ActivationCases))]
    public void Accept_Eip7702IsNotActivated_ReturnsExpected(bool isActive, AcceptTxResult expected)
    {
        IReleaseSpec spec = isActive ? Prague.Instance : Cancun.Instance;
        (TxDistinctSortedPool standardPool, TxDistinctSortedPool blobPool) = CreatePools();
        Transaction inPool = Build.A.Transaction.WithNonce(0).SignedAndResolved(Ecdsa, TestItem.PrivateKeyA).TestObject;
        standardPool.TryInsert(inPool.Hash, inPool);
        TestReadOnlyStateProvider stateProvider = CreateDelegatedStateProvider();
        DelegatedAccountFilter filter = CreateFilter(spec, standardPool, blobPool, stateProvider);
        Transaction transaction = Build.A.Transaction.WithNonce(1).SignedAndResolved(Ecdsa, TestItem.PrivateKeyA).TestObject;
        TxFilteringState state = new(transaction, stateProvider);

        AcceptTxResult result = filter.Accept(transaction, ref state, TxHandlingOptions.None);

        Assert.That(result, Is.EqualTo(expected));
    }

    private static readonly object[] PendingDelegationNonceCases =
    {
        new object[] { 0, AcceptTxResult.NotCurrentNonceForDelegation },
        new object[] { 1, AcceptTxResult.Accepted },
        new object[] { 2, AcceptTxResult.NotCurrentNonceForDelegation },
    };

    [TestCaseSource(nameof(PendingDelegationNonceCases))]
    public void Accept_SenderHasPendingDelegation_OnlyAcceptsIfNonceIsExactMatch(int nonce, AcceptTxResult expected)
    {
        (TxDistinctSortedPool standardPool, TxDistinctSortedPool blobPool) = CreatePools();
        DelegationCache pendingDelegations = new();
        pendingDelegations.IncrementDelegationCount(TestItem.AddressA);
        DelegatedAccountFilter filter = CreateFilter(Prague.Instance, standardPool, blobPool, delegationCache: pendingDelegations);
        Transaction transaction = Build.A.Transaction.WithNonce((UInt256)nonce).SignedAndResolved(Ecdsa, TestItem.PrivateKeyA).TestObject;
        TestReadOnlyStateProvider stateProvider = new();
        stateProvider.CreateAccount(TestItem.AddressA, 0, 1);
        TxFilteringState state = new(transaction, stateProvider);

        AcceptTxResult result = filter.Accept(transaction, ref state, TxHandlingOptions.None);

        Assert.That(result, Is.EqualTo(expected));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Accept_AuthorityHasPendingTransaction_ReturnsDelegatorHasPendingTx(bool useBlobPool)
    {
        (TxDistinctSortedPool standardPool, TxDistinctSortedPool blobPool) = CreatePools();
        DelegatedAccountFilter filter = CreateFilter(Prague.Instance, standardPool, blobPool);
        if (useBlobPool)
        {
            Transaction transaction = Build.A.Transaction
                .WithShardBlobTxTypeAndFields()
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            blobPool.TryInsert(transaction.Hash, transaction, out _);
        }
        else
        {
            Transaction transaction = Build.A.Transaction
                .WithNonce(0)
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            standardPool.TryInsert(transaction.Hash, transaction, out _);
        }
        TxFilteringState state = new();
        AuthorizationTuple authTuple = new(0, TestItem.AddressB, 0, new Core.Crypto.Signature(0, 0, 27), TestItem.AddressA);
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
        (TxDistinctSortedPool standardPool, TxDistinctSortedPool blobPool) = CreatePools();
        DelegationCache pendingDelegations = new();
        pendingDelegations.IncrementDelegationCount(TestItem.AddressA);
        DelegatedAccountFilter filter = CreateFilter(Prague.Instance, standardPool, blobPool, delegationCache: pendingDelegations);
        Transaction transaction = Build.A.Transaction
            .WithNonce(1)
            .SignedAndResolved(Ecdsa, TestItem.PrivateKeyA).TestObject;
        standardPool.TryInsert(transaction.Hash, transaction);
        Transaction setCodeTransaction = Build.A.Transaction
            .WithNonce(0)
            .WithType(TxType.SetCode)
            .WithMaxFeePerGas(9.GWei)
            .WithMaxPriorityFeePerGas(9.GWei)
            .WithGasLimit(100_000)
            .WithAuthorizationCode(new AuthorizationTuple(0, TestItem.AddressC, 0, new Core.Crypto.Signature(new byte[64], 0), TestItem.AddressA))
            .WithTo(TestItem.AddressB)
            .SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        TxFilteringState state = new();

        AcceptTxResult result = filter.Accept(setCodeTransaction, ref state, TxHandlingOptions.None);

        Assert.That(result, Is.EqualTo(AcceptTxResult.DelegatorHasPendingTx));
    }
}
