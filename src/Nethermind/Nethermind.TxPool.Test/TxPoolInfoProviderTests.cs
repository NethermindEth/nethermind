// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.TxPool.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class TxPoolInfoProviderTests
{
    private Address _address;
    private IAccountStateProvider _stateReader;
    private ITxPoolInfoProvider _infoProvider;
    private ITxPool _txPool;

    [SetUp]
    public void Setup()
    {
        _address = Address.FromNumber(1);
        _stateReader = Substitute.For<IAccountStateProvider>();
        _txPool = Substitute.For<ITxPool>();
        _txPool.GetPendingTransactionsBySender(Arg.Any<Address>()).Returns([]);
        _txPool.GetPendingLightBlobTransactionsBySender(Arg.Any<Address>()).Returns([]);
        _txPool.GetPendingLightBlobTransactionsBySender().Returns(new Dictionary<AddressAsKey, Transaction[]>());
        _infoProvider = new TxPoolInfoProvider(_stateReader, _txPool);
    }

    [Test]
    public void should_return_valid_pending_and_queued_transactions()
    {
        uint nonce = 3;
        _stateReader.GetNonce(_address).Returns(nonce);
        Transaction[] transactions = GetTransactions();

        _txPool.GetPendingTransactionsBySender()
            .Returns(new Dictionary<AddressAsKey, Transaction[]> { { _address, transactions } });
        TxPoolInfo info = _infoProvider.GetInfo();

        Assert.That(info.Pending.Count, Is.EqualTo(1));
        Assert.That(info.Queued.Count, Is.EqualTo(1));

        KeyValuePair<AddressAsKey, IDictionary<ulong, Transaction>> pending = info.Pending.First();
        Assert.That(pending.Key.Value, Is.EqualTo(_address));
        Assert.That(pending.Value.Count, Is.EqualTo(3));
        VerifyNonceAndTransactions(pending.Value, 3);
        VerifyNonceAndTransactions(pending.Value, 4);
        VerifyNonceAndTransactions(pending.Value, 5);

        KeyValuePair<AddressAsKey, IDictionary<ulong, Transaction>> queued = info.Queued.First();
        Assert.That(queued.Key.Value, Is.EqualTo(_address));
        Assert.That(queued.Value.Count, Is.EqualTo(4));
        VerifyNonceAndTransactions(queued.Value, 1);
        VerifyNonceAndTransactions(queued.Value, 2);
        VerifyNonceAndTransactions(queued.Value, 8);
        VerifyNonceAndTransactions(queued.Value, 9);
    }

    [Test]
    public void GetInfo_WhenSenderHasStandardAndBlobTransactions_OmitsBlobs()
    {
        _stateReader.GetNonce(_address).Returns(0UL);
        Transaction[] standard = BuildTransactions([0, 2]);
        Transaction[] blobs = BuildTransactions([1, 3]);
        _txPool.GetPendingTransactionsBySender()
            .Returns(new Dictionary<AddressAsKey, Transaction[]> { { _address, standard } });
        // Blob bucket is populated to make the exclusion semantics explicit; GetInfo does not
        // consult the blob pool, so these nonces must not appear in the output.
        _txPool.GetPendingLightBlobTransactionsBySender()
            .Returns(new Dictionary<AddressAsKey, Transaction[]> { { _address, blobs } });

        TxPoolInfo info = _infoProvider.GetInfo();

        Assert.That(info.Pending[_address].Keys, Is.EqualTo(new ulong[] { 0 }));
        Assert.That(info.Queued[_address].Keys, Is.EqualTo(new ulong[] { 2 }));
    }

    [Test]
    public void GetInfo_WhenSenderHasOnlyBlobTransactions_DoesNotAppearInResult()
    {
        // Regression guard: if anyone re-introduces a blob lookup in GetInfo, the blob mock
        // here becomes live and the address would appear in Pending/Queued, failing the
        // NotContainKey assertions. Today the blob mock is unconsulted (matches geth's
        // BlobPool.Content() empty-stub behaviour), so the address is absent because the
        // standard-pool dictionary has no entry for it.
        _stateReader.GetNonce(_address).Returns(0UL);
        Transaction[] blobs = BuildTransactions([0, 1]);
        _txPool.GetPendingLightBlobTransactionsBySender()
            .Returns(new Dictionary<AddressAsKey, Transaction[]> { { _address, blobs } });

        TxPoolInfo info = _infoProvider.GetInfo();

        Assert.That(info.Pending.ContainsKey(_address), Is.False);
        Assert.That(info.Queued.ContainsKey(_address), Is.False);
    }

    // Inputs are always nonce-sorted: TxDistinctSortedPool's group comparer puts
    // CompareTxByNonce.Instance first (see TxSortedPoolExtensions.GetPoolUniqueTxComparerByNonce),
    // so per-sender bucket arrays come back sorted. TxPoolInfoProvider relies on that.
    private static IEnumerable<TestCaseData> SenderInfoCases() =>
    [
        new TestCaseData(
                new SenderScenario(AccountNonce: 3, TxNonces: [1, 2, 3, 4, 5, 8, 9],
                    ExpectedPending: [3, 4, 5], ExpectedQueued: [1, 2, 8, 9]))
            .SetName("MixedNoncesAroundAccountNonce_SplitsIntoPendingAndQueued"),

        new TestCaseData(
                new SenderScenario(AccountNonce: 0, TxNonces: [0, 1, 2],
                    ExpectedPending: [0, 1, 2], ExpectedQueued: []))
            .SetName("AllNoncesContinuousFromAccount_AllPending"),

        new TestCaseData(
                new SenderScenario(AccountNonce: 5, TxNonces: [10, 11],
                    ExpectedPending: [], ExpectedQueued: [10, 11]))
            .SetName("AllNoncesAheadOfAccount_AllQueued"),
    ];

    [TestCaseSource(nameof(SenderInfoCases))]
    public void GetSenderInfo_WhenSenderHasTransactions_SplitsByNonceAgainstAccount(SenderScenario scenario)
    {
        _stateReader.GetNonce(_address).Returns(scenario.AccountNonce);
        _txPool.GetPendingTransactionsBySender(_address).Returns(BuildTransactions(scenario.TxNonces));

        TxPoolSenderInfo senderInfo = _infoProvider.GetSenderInfo(_address);

        Assert.That(senderInfo.Pending.Keys, Is.EqualTo(scenario.ExpectedPending), "pending nonces are those continuous with the account nonce");
        Assert.That(senderInfo.Queued.Keys, Is.EqualTo(scenario.ExpectedQueued), "queued nonces are those beyond a gap from the account nonce");
    }

    [Test]
    public void GetSenderInfo_WhenSenderHasNoTransactions_ReturnsEmpty()
    {
        TxPoolSenderInfo senderInfo = _infoProvider.GetSenderInfo(_address);

        Assert.That(senderInfo, Is.SameAs(TxPoolSenderInfo.Empty), "the empty singleton avoids allocating two empty dictionaries on the miss path");
    }

    [Test]
    public void GetSenderInfo_WhenSenderHasStandardAndBlobTransactions_OmitsBlobs()
    {
        _stateReader.GetNonce(_address).Returns(0UL);
        _txPool.GetPendingTransactionsBySender(_address).Returns(BuildTransactions([0, 2]));
        // Blob bucket is populated to make the exclusion semantics explicit; GetSenderInfo
        // does not consult the blob pool, so these nonces must not appear in the output.
        _txPool.GetPendingLightBlobTransactionsBySender(_address).Returns(BuildTransactions([1, 3]));

        TxPoolSenderInfo senderInfo = _infoProvider.GetSenderInfo(_address);

        Assert.That(senderInfo.Pending.Keys, Is.EqualTo(new ulong[] { 0 }));
        Assert.That(senderInfo.Queued.Keys, Is.EqualTo(new ulong[] { 2 }));
    }

    [Test]
    public void GetSenderInfo_WhenSenderHasOnlyBlobTransactions_ReturnsEmpty()
    {
        // Regression guard: if anyone re-introduces a blob lookup in GetSenderInfo, the blob
        // mock here becomes live and the result stops being TxPoolSenderInfo.Empty, failing
        // the assertion. Today the blob mock is unconsulted (matches geth's BlobPool.Content()
        // empty-stub behaviour), so the empty result comes from the standard pool being empty.
        _stateReader.GetNonce(_address).Returns(0UL);
        _txPool.GetPendingLightBlobTransactionsBySender(_address).Returns(BuildTransactions([0, 1]));

        TxPoolSenderInfo senderInfo = _infoProvider.GetSenderInfo(_address);

        Assert.That(senderInfo, Is.SameAs(TxPoolSenderInfo.Empty));
    }

    [Test]
    public void GetSenderInfo_WhenCalled_DoesNotScanFullPool()
    {
        _infoProvider.GetSenderInfo(_address);

        _txPool.DidNotReceive().GetPendingTransactionsBySender();
        _txPool.DidNotReceive().GetPendingLightBlobTransactionsBySender();
    }

    private static IEnumerable<TestCaseData> CountCases() =>
    [
        new TestCaseData(
                new SenderScenario(AccountNonce: 3, TxNonces: [1, 2, 3, 4, 5, 8, 9],
                    ExpectedPending: [3, 4, 5], ExpectedQueued: [1, 2, 8, 9]))
            .SetName("MixedNonces_CountsMatchSplit"),

        new TestCaseData(
                new SenderScenario(AccountNonce: 0, TxNonces: [0, 1, 2],
                    ExpectedPending: [0, 1, 2], ExpectedQueued: []))
            .SetName("AllPending_QueuedIsZero"),

        new TestCaseData(
                new SenderScenario(AccountNonce: 5, TxNonces: [10, 11],
                    ExpectedPending: [], ExpectedQueued: [10, 11]))
            .SetName("AllQueued_PendingIsZero"),
    ];

    [TestCaseSource(nameof(CountCases))]
    public void GetCounts_WhenPoolHasOneSender_ReturnsPendingAndQueuedTotals(SenderScenario scenario)
    {
        _stateReader.GetNonce(_address).Returns(scenario.AccountNonce);
        _txPool.GetPendingTransactionsBySender()
            .Returns(new Dictionary<AddressAsKey, Transaction[]> { { _address, BuildTransactions(scenario.TxNonces) } });

        TxPoolCounts counts = _infoProvider.GetCounts();

        Assert.That(counts.Pending, Is.EqualTo(scenario.ExpectedPending.Length), "pending count must match the split");
        Assert.That(counts.Queued, Is.EqualTo(scenario.ExpectedQueued.Length), "queued count must match the split");
    }

    [Test]
    public void GetCounts_WhenSenderHasStandardAndBlob_CountsAcrossBothPools()
    {
        _stateReader.GetNonce(_address).Returns(0UL);
        _txPool.GetPendingTransactionsBySender()
            .Returns(new Dictionary<AddressAsKey, Transaction[]> { { _address, BuildTransactions([0, 2]) } });
        _txPool.GetPendingLightBlobTransactionsBySender()
            .Returns(new Dictionary<AddressAsKey, Transaction[]> { { _address, BuildTransactions([1, 5]) } });

        TxPoolCounts counts = _infoProvider.GetCounts();

        Assert.That(counts.Pending, Is.EqualTo(3), "nonces 0, 1, 2 form a continuous run from account nonce 0");
        Assert.That(counts.Queued, Is.EqualTo(1), "nonce 5 is queued behind the gap");
    }

    private void VerifyNonceAndTransactions(IDictionary<ulong, Transaction> transactionNonce, ulong nonce) =>
        Assert.That(transactionNonce[nonce].Nonce, Is.EqualTo(nonce));

    private Transaction[] GetTransactions() =>
        BuildTransactions([1, 2, 3, 4, 5, 8, 9]);

    private Transaction[] BuildTransactions(ulong[] nonces)
    {
        Transaction[] result = new Transaction[nonces.Length];
        for (int i = 0; i < nonces.Length; i++)
            result[i] = Build.A.Transaction.WithNonce(nonces[i]).WithSenderAddress(_address).TestObject;
        return result;
    }

    public record SenderScenario(uint AccountNonce, ulong[] TxNonces, ulong[] ExpectedPending, ulong[] ExpectedQueued);
}
