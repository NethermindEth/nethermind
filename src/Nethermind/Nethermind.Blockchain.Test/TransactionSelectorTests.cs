// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using Nethermind.TxPool;
using Nethermind.TxPool.Comparison;
using NSubstitute;
using NUnit.Framework;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Crypto;

namespace Nethermind.Blockchain.Test
{
    public class TransactionSelectorTests
    {
        public static IEnumerable ProperTransactionsSelectedTestCases
        {
            get
            {
                ProperTransactionsSelectedTestCase allTransactionsSelected = ProperTransactionsSelectedTestCase.Default;
                allTransactionsSelected.ExpectedSelectedTransactions.AddRange(
                    allTransactionsSelected.Transactions.OrderBy(static t => t.Nonce));
                yield return new TestCaseData(allTransactionsSelected).SetName("All transactions selected");

                ProperTransactionsSelectedTestCase twoTransactionSelectedDueToLackOfSenderAddress =
                    ProperTransactionsSelectedTestCase.Default;
                twoTransactionSelectedDueToLackOfSenderAddress.Transactions.First().SenderAddress = null;
                twoTransactionSelectedDueToLackOfSenderAddress.ExpectedSelectedTransactions.AddRange(
                    twoTransactionSelectedDueToLackOfSenderAddress.Transactions.OrderBy(static t => t.Nonce).Take(2));
                yield return new TestCaseData(twoTransactionSelectedDueToLackOfSenderAddress).SetName(
                    "Two transaction selected due to lack of sender address");
            }
        }

        public static IEnumerable Eip1559LegacyTransactionTestCases
        {
            get
            {
                ProperTransactionsSelectedTestCase allTransactionsSelected = ProperTransactionsSelectedTestCase.Eip1559DefaultLegacyTransactions;
                allTransactionsSelected.BaseFee = 0;
                allTransactionsSelected.ExpectedSelectedTransactions.AddRange(
                    allTransactionsSelected.Transactions.OrderBy(t => t.Nonce));
                yield return new TestCaseData(allTransactionsSelected).SetName("Legacy transactions: All transactions selected - 0 BaseFee");

                ProperTransactionsSelectedTestCase baseFeeLowerThanGasPrice = ProperTransactionsSelectedTestCase.Eip1559DefaultLegacyTransactions;
                baseFeeLowerThanGasPrice.BaseFee = 5;
                baseFeeLowerThanGasPrice.ExpectedSelectedTransactions.AddRange(
                    baseFeeLowerThanGasPrice.Transactions.OrderBy(t => t.Nonce));
                yield return new TestCaseData(baseFeeLowerThanGasPrice).SetName("Legacy transactions: All transactions selected - BaseFee lower than gas price");

                ProperTransactionsSelectedTestCase baseFeeGreaterThanGasPrice = ProperTransactionsSelectedTestCase.Eip1559DefaultLegacyTransactions;
                baseFeeGreaterThanGasPrice.BaseFee = 1.GWei();
                yield return new TestCaseData(baseFeeGreaterThanGasPrice).SetName("Legacy transactions: None transactions selected - BaseFee greater than gas price");

                ProperTransactionsSelectedTestCase balanceCheckWithTxValue = new()
                {
                    ReleaseSpec = London.Instance,
                    BaseFee = 5,
                    AccountStates = { { TestItem.AddressA, (300, 1) } },
                    Transactions =
                    {
                        Build.A.Transaction.WithSenderAddress(TestItem.AddressA).WithNonce(2)
                            .WithGasPrice(5).WithGasLimit(10).SignedAndResolved(TestItem.PrivateKeyA).TestObject,
                        Build.A.Transaction.WithSenderAddress(TestItem.AddressA).WithNonce(1)
                            .WithGasPrice(20).WithGasLimit(10).WithValue(100).SignedAndResolved(TestItem.PrivateKeyA).TestObject
                    },
                    GasLimit = 10000000
                };
                balanceCheckWithTxValue.ExpectedSelectedTransactions.AddRange(
                    new[] { 1 }.Select(i => balanceCheckWithTxValue.Transactions[i]));
                yield return new TestCaseData(balanceCheckWithTxValue).SetName("Legacy transactions: one transaction selected because of account balance");
            }
        }

        public static IEnumerable Eip1559TestCases
        {
            get
            {
                ProperTransactionsSelectedTestCase allTransactionsSelected = ProperTransactionsSelectedTestCase.Eip1559Default;
                allTransactionsSelected.BaseFee = 0;
                allTransactionsSelected.ExpectedSelectedTransactions.AddRange(
                    allTransactionsSelected.Transactions.OrderBy(t => t.Nonce));
                yield return new TestCaseData(allTransactionsSelected).SetName("EIP1559 transactions: All transactions selected - 0 BaseFee");

                ProperTransactionsSelectedTestCase baseFeeLowerThanGasPrice = ProperTransactionsSelectedTestCase.Eip1559Default;
                baseFeeLowerThanGasPrice.BaseFee = 5;
                baseFeeLowerThanGasPrice.ExpectedSelectedTransactions.AddRange(
                    baseFeeLowerThanGasPrice.Transactions.OrderBy(t => t.Nonce));
                yield return new TestCaseData(baseFeeLowerThanGasPrice).SetName("EIP1559 transactions: All transactions selected - BaseFee lower than gas price");

                ProperTransactionsSelectedTestCase baseFeeGreaterThanGasPrice = ProperTransactionsSelectedTestCase.Eip1559Default;
                baseFeeGreaterThanGasPrice.BaseFee = 1.GWei();
                yield return new TestCaseData(baseFeeGreaterThanGasPrice).SetName("EIP1559 transactions: None transactions selected - BaseFee greater than gas price");

                ProperTransactionsSelectedTestCase balanceCheckWithTxValue = new()
                {
                    ReleaseSpec = London.Instance,
                    BaseFee = 5,
                    AccountStates = { { TestItem.AddressA, (400, 1) } },
                    Transactions =
                    {
                        Build.A.Transaction.WithSenderAddress(TestItem.AddressA).WithType(TxType.EIP1559).WithNonce(2)
                            .WithMaxFeePerGas(4).WithGasLimit(10).SignedAndResolved(TestItem.PrivateKeyA).TestObject,
                        Build.A.Transaction.WithSenderAddress(TestItem.AddressA).WithType(TxType.EIP1559).WithNonce(1)
                            .WithMaxFeePerGas(30).WithGasLimit(10).WithValue(100).SignedAndResolved(TestItem.PrivateKeyA).TestObject
                    },
                    GasLimit = 10000000
                };
                balanceCheckWithTxValue.ExpectedSelectedTransactions.AddRange(
                    new[] { 1 }.Select(i => balanceCheckWithTxValue.Transactions[i]));
                yield return new TestCaseData(balanceCheckWithTxValue).SetName("EIP1559 transactions: one transaction selected because of account balance");

                ProperTransactionsSelectedTestCase balanceCheckWithGasPremium = new()
                {
                    ReleaseSpec = London.Instance,
                    BaseFee = 5,
                    AccountStates = { { TestItem.AddressA, (400, 1) } },
                    Transactions =
                    {
                        Build.A.Transaction.WithSenderAddress(TestItem.AddressA).WithType(TxType.EIP1559).WithNonce(2)
                            .WithMaxFeePerGas(5).WithGasLimit(10).SignedAndResolved(TestItem.PrivateKeyA).TestObject,
                        Build.A.Transaction.WithSenderAddress(TestItem.AddressA).WithNonce(1)
                            .WithMaxFeePerGas(30).WithMaxPriorityFeePerGas(25).WithGasLimit(10).WithType(TxType.EIP1559).WithValue(60).SignedAndResolved(TestItem.PrivateKeyA).TestObject,
                    },
                    GasLimit = 10000000
                };
                balanceCheckWithGasPremium.ExpectedSelectedTransactions.AddRange(
                    new[] { 1 }.Select(i => balanceCheckWithGasPremium.Transactions[i]));
                yield return new TestCaseData(balanceCheckWithGasPremium).SetName("EIP1559 transactions: one transaction selected because of account balance and miner tip");
            }
        }

        public static IEnumerable EnoughShardBlobTransactionsSelectedTestCases
        {
            get
            {
                ProperTransactionsSelectedTestCase maxTransactionsSelected = ProperTransactionsSelectedTestCase.Eip1559Default;
                maxTransactionsSelected.ReleaseSpec = Cancun.Instance;
                maxTransactionsSelected.BaseFee = 1;
                maxTransactionsSelected.Transactions.ForEach(static tx =>
                {
                    tx.Type = TxType.Blob;
                    tx.BlobVersionedHashes = new byte[1][];
                    tx.MaxFeePerBlobGas = 1;
                    tx.NetworkWrapper = new ShardBlobNetworkWrapper(new byte[1][], new byte[1][], new byte[1][], ProofVersion.V0);
                });
                maxTransactionsSelected.Transactions[1].BlobVersionedHashes =
                    new byte[maxTransactionsSelected.ReleaseSpec.MaxBlobCount - 1][];
                maxTransactionsSelected.ExpectedSelectedTransactions.AddRange(
                    maxTransactionsSelected.Transactions.OrderBy(static t => t.Nonce).Take(2));
                yield return new TestCaseData(maxTransactionsSelected).SetName("Enough transactions selected");

                ProperTransactionsSelectedTestCase enoughTransactionsSelected =
                    ProperTransactionsSelectedTestCase.Eip1559Default;
                enoughTransactionsSelected.ReleaseSpec = Cancun.Instance;
                enoughTransactionsSelected.BaseFee = 1;

                Transaction[] expectedSelectedTransactions =
                    enoughTransactionsSelected.Transactions.OrderBy(static t => t.Nonce).ToArray();
                expectedSelectedTransactions[0].Type = TxType.Blob;
                expectedSelectedTransactions[0].BlobVersionedHashes =
                    new byte[enoughTransactionsSelected.ReleaseSpec.MaxBlobCount][];
                expectedSelectedTransactions[0].MaxFeePerBlobGas = 1;
                expectedSelectedTransactions[0].NetworkWrapper =
                    new ShardBlobNetworkWrapper(new byte[1][], new byte[1][], new byte[1][], ProofVersion.V0);
                expectedSelectedTransactions[1].Type = TxType.Blob;
                expectedSelectedTransactions[1].BlobVersionedHashes = new byte[1][];
                expectedSelectedTransactions[1].MaxFeePerBlobGas = 1;
                expectedSelectedTransactions[1].NetworkWrapper =
                    new ShardBlobNetworkWrapper(new byte[1][], new byte[1][], new byte[1][], ProofVersion.V0);
                enoughTransactionsSelected.ExpectedSelectedTransactions.AddRange(
                    expectedSelectedTransactions.Where(static (_, index) => index != 1));
                yield return new TestCaseData(enoughTransactionsSelected).SetName(
                    "Enough shard blob transactions and others selected");

                ProperTransactionsSelectedTestCase higherPriorityTransactionsSelected = ProperTransactionsSelectedTestCase.Eip1559Default;
                var accounts = higherPriorityTransactionsSelected.AccountStates;
                accounts[TestItem.AddressA] = (1000, 0);
                accounts[TestItem.AddressB] = (1000, 0);
                accounts[TestItem.AddressC] = (1000, 0);
                accounts[TestItem.AddressD] = (1000, 0);
                accounts[TestItem.AddressE] = (1000, 0);
                accounts[TestItem.AddressF] = (1000, 0);
                higherPriorityTransactionsSelected.ReleaseSpec = Cancun.Instance;
                higherPriorityTransactionsSelected.BaseFee = 1;
                higherPriorityTransactionsSelected.Transactions =
                [
                    // This tx should be rejected in preference for the other 5 even though its fee is much higher
                    CreateBlobTransaction(TestItem.AddressA, TestItem.PrivateKeyA, maxFee: 89, blobCount: 5),
                    // As total of other 5 below is higher
                    CreateBlobTransaction(TestItem.AddressB, TestItem.PrivateKeyB, maxFee: 16, blobCount: 1),
                    CreateBlobTransaction(TestItem.AddressC, TestItem.PrivateKeyC, maxFee: 18, blobCount: 1),
                    CreateBlobTransaction(TestItem.AddressD, TestItem.PrivateKeyD, maxFee: 17, blobCount: 1),
                    CreateBlobTransaction(TestItem.AddressE, TestItem.PrivateKeyE, maxFee: 19, blobCount: 1),
                    CreateBlobTransaction(TestItem.AddressF, TestItem.PrivateKeyF, maxFee: 20, blobCount: 1),
                ];

                higherPriorityTransactionsSelected.ExpectedSelectedTransactions.AddRange(
                    higherPriorityTransactionsSelected.Transactions.Where(tx => tx.GetBlobCount() == 1)
                    .OrderByDescending(t => t.MaxFeePerGas).Take(5));

                var rnd = new Random(12345);
                for (int i = 0; i < 20; i++)
                {
                    yield return new TestCaseData(higherPriorityTransactionsSelected)
                        .SetName($"Correct priority blobs - Order {i:00}");
                    // The selection should be the same regardless of the order of the txs
                    // as the packing rules should win
                    higherPriorityTransactionsSelected.Transactions.Shuffle(rnd);
                }
            }
        }

        private static Transaction CreateBlobTransaction(Address address, PrivateKey key, UInt256 maxFee, int blobCount)
            => CreateBlobTransaction(address, key, maxFee, blobCount, nonce: 1);

        private static Transaction CreateBlobTransaction(Address address, PrivateKey key, UInt256 maxFee, int blobCount, UInt256 nonce, uint priority = 1)
        {
            return Build.A.Transaction
                .WithSenderAddress(address)
                .WithShardBlobTxTypeAndFields(blobCount)
                .WithNonce(nonce)
                .WithMaxFeePerGas(maxFee)
                .WithMaxPriorityFeePerGas(priority)
                .WithGasLimit(20)
                .SignedAndResolved(key).TestObject;
        }

        public static IEnumerable BlobTransactionOrderingTestCases
        {
            get
            {
                (Address address, PrivateKey key)[] Accounts =
                {
                    (TestItem.AddressA, TestItem.PrivateKeyA),
                    (TestItem.AddressB, TestItem.PrivateKeyB)
                };

                {
                    var blobTxs = CreateTestCase();
                    var txs = new List<Transaction>();

                    UInt256 nonce = 1;
                    AddTxs(txCount: 5, blobsPerTx: 5, account: 0, txs, ref nonce);
                    AddTxs(txCount: 7, blobsPerTx: 1, account: 0, txs, ref nonce);

                    blobTxs.Transactions = txs;

                    blobTxs.ExpectedSelectedTransactions.AddRange(blobTxs.Transactions.Take(1));

                    yield return new TestCaseData(blobTxs).SetName("Blob Transaction Ordering, Single Account, Single Blob");
                }
                {
                    var blobTxs = CreateTestCase();
                    var txs = new List<Transaction>();

                    UInt256 nonce = 1;
                    AddTxs(txCount: 5, blobsPerTx: 5, account: 0, txs, ref nonce);
                    AddTxs(txCount: 1, blobsPerTx: 2, account: 0, txs, ref nonce);
                    AddTxs(txCount: 5, blobsPerTx: 1, account: 0, txs, ref nonce);

                    blobTxs.Transactions = txs;

                    blobTxs.ExpectedSelectedTransactions.AddRange(blobTxs.Transactions.Take(1));

                    yield return new TestCaseData(blobTxs).SetName("Blob Transaction Ordering, Single Account, Dual Blob");
                }
                {
                    var blobTxs = CreateTestCase();
                    var txs = new List<Transaction>();

                    UInt256 nonce = 1;
                    AddTxs(txCount: 5, blobsPerTx: 5, account: 0, txs, ref nonce);
                    nonce = 1;
                    AddTxs(txCount: 5, blobsPerTx: 1, account: 1, txs, ref nonce);

                    blobTxs.Transactions = txs;

                    blobTxs.ExpectedSelectedTransactions.AddRange(blobTxs.Transactions.Take(1));
                    blobTxs.ExpectedSelectedTransactions.AddRange(blobTxs.Transactions.Skip(5).Take(1));

                    yield return new TestCaseData(blobTxs).SetName("Blob Transaction Ordering, Multiple Accounts");
                }
                {
                    var blobTxs = CreateTestCase();
                    var txs = new List<Transaction>();

                    UInt256 nonce0 = 1;
                    AddTxs(txCount: 5, blobsPerTx: 5, account: 0, txs, ref nonce0);
                    UInt256 nonce1 = 2;
                    AddTxs(txCount: 5, blobsPerTx: 3, account: 1, txs, ref nonce1);
                    AddTxs(txCount: 5, blobsPerTx: 1, account: 0, txs, ref nonce0);

                    blobTxs.Transactions = txs;

                    blobTxs.ExpectedSelectedTransactions.AddRange(blobTxs.Transactions.Skip(5).Take(2));

                    yield return new TestCaseData(blobTxs).SetName("Blob Transaction Ordering, Multiple Accounts, Nonce Order 1");
                }
                {
                    var blobTxs = CreateTestCase();
                    var txs = new List<Transaction>();

                    UInt256 nonce0 = 1;
                    AddTxs(txCount: 1, blobsPerTx: 5, account: 0, txs, ref nonce0);
                    UInt256 nonce1 = 1;
                    AddTxs(txCount: 5, blobsPerTx: 4, account: 1, txs, ref nonce1);
                    AddTxs(txCount: 3, blobsPerTx: 1, account: 0, txs, ref nonce0);

                    blobTxs.Transactions = txs;

                    blobTxs.ExpectedSelectedTransactions.AddRange(blobTxs.Transactions.Take(1));
                    blobTxs.ExpectedSelectedTransactions.AddRange(blobTxs.Transactions.Skip(6).Take(1));

                    yield return new TestCaseData(blobTxs).SetName("Blob Transaction Ordering, Multiple Accounts, Nonce Order 2");
                }
                {
                    var blobTxs = CreateTestCase();
                    var txs = new List<Transaction>();

                    UInt256 nonce0 = 1;
                    AddTxs(txCount: 1, blobsPerTx: 5, account: 0, txs, ref nonce0, priority: 1);
                    UInt256 nonce1 = 1;
                    AddTxs(txCount: 2, blobsPerTx: 2, account: 1, txs, ref nonce1, priority: 1);
                    AddTxs(txCount: 3, blobsPerTx: 2, account: 0, txs, ref nonce0, priority: 1);

                    blobTxs.Transactions = txs;

                    blobTxs.ExpectedSelectedTransactions.AddRange(blobTxs.Transactions.Skip(1).Take(2));

                    yield return new TestCaseData(blobTxs).SetName("Blob Transaction Ordering, Multiple Accounts, Nonce Order 3");
                }
                {
                    var blobTxs = CreateTestCase();
                    var txs = new List<Transaction>();

                    UInt256 nonce0 = 1;
                    AddTxs(txCount: 1, blobsPerTx: 5, account: 0, txs, ref nonce0, priority: 1);
                    UInt256 nonce1 = 1;
                    AddTxs(txCount: 2, blobsPerTx: 2, account: 1, txs, ref nonce1, priority: 1);
                    AddTxs(txCount: 3, blobsPerTx: 1, account: 0, txs, ref nonce0, priority: 1);

                    blobTxs.Transactions = txs;

                    blobTxs.ExpectedSelectedTransactions.AddRange(blobTxs.Transactions.Skip(1).Take(2));

                    yield return new TestCaseData(blobTxs).SetName("Blob Transaction Ordering, Multiple Accounts, Nonce Order 4");
                }
                {
                    var blobTxs = CreateTestCase();
                    var txs = new List<Transaction>();

                    UInt256 nonce0 = 1;
                    AddTxs(txCount: 1, blobsPerTx: 5, account: 0, txs, ref nonce0, priority: 1);
                    AddTxs(txCount: 3, blobsPerTx: 1, account: 0, txs, ref nonce0, priority: 1);
                    UInt256 nonce1 = 1;
                    AddTxs(txCount: 2, blobsPerTx: 2, account: 1, txs, ref nonce1, priority: 1);

                    blobTxs.Transactions = txs;

                    blobTxs.ExpectedSelectedTransactions.AddRange(blobTxs.Transactions.Skip(4).Take(2));

                    yield return new TestCaseData(blobTxs).SetName("Blob Transaction Ordering, Multiple Accounts, Nonce Order 5a");
                }
                {
                    var blobTxs = CreateTestCase();
                    var txs = new List<Transaction>();

                    UInt256 nonce0 = 1;
                    AddTxs(txCount: 2, blobsPerTx: 2, account: 0, txs, ref nonce0, priority: 1);
                    UInt256 nonce1 = 1;
                    AddTxs(txCount: 1, blobsPerTx: 5, account: 1, txs, ref nonce1, priority: 1);
                    AddTxs(txCount: 3, blobsPerTx: 1, account: 1, txs, ref nonce1, priority: 1);

                    blobTxs.Transactions = txs;

                    blobTxs.ExpectedSelectedTransactions.AddRange(blobTxs.Transactions.Take(2));

                    yield return new TestCaseData(blobTxs).SetName("Blob Transaction Ordering, Multiple Accounts, Nonce Order 5b");
                }
                {
                    var blobTxs = CreateTestCase();
                    var txs = new List<Transaction>();

                    UInt256 nonce0 = 1;
                    AddTxs(txCount: 1, blobsPerTx: 5, account: 0, txs, ref nonce0, priority: 1);
                    UInt256 nonce1 = 1;
                    AddTxs(txCount: 1, blobsPerTx: 5, account: 1, txs, ref nonce1, priority: 1);
                    AddTxs(txCount: 3, blobsPerTx: 1, account: 0, txs, ref nonce0, priority: 1);

                    blobTxs.Transactions = txs;

                    blobTxs.ExpectedSelectedTransactions.AddRange(blobTxs.Transactions.Take(1));
                    blobTxs.ExpectedSelectedTransactions.AddRange(blobTxs.Transactions.Skip(2).Take(1));

                    yield return new TestCaseData(blobTxs).SetName("Blob Transaction Ordering, Multiple Accounts, Nonce Order 6");
                }
                {
                    var blobTxs = CreateTestCase();
                    var txs = new List<Transaction>();

                    UInt256 nonce0 = 1;
                    AddTxs(txCount: 1, blobsPerTx: 5, account: 0, txs, ref nonce0, priority: 1);
                    UInt256 nonce1 = 1;
                    AddTxs(txCount: 1, blobsPerTx: 5, account: 1, txs, ref nonce1, priority: 1);
                    AddTxs(txCount: 3, blobsPerTx: 1, account: 1, txs, ref nonce1, priority: 1);

                    blobTxs.Transactions = txs;
                    blobTxs.ExpectedSelectedTransactions.AddRange(blobTxs.Transactions.Skip(1).Take(2));

                    yield return new TestCaseData(blobTxs).SetName("Blob Transaction Ordering, Multiple Accounts, Nonce Order 7a");
                }
                {
                    var blobTxs = CreateTestCase();
                    var txs = new List<Transaction>();

                    UInt256 nonce1 = 1;
                    AddTxs(txCount: 1, blobsPerTx: 5, account: 1, txs, ref nonce1, priority: 1);
                    UInt256 nonce0 = 1;
                    AddTxs(txCount: 1, blobsPerTx: 5, account: 0, txs, ref nonce0, priority: 1);
                    AddTxs(txCount: 3, blobsPerTx: 1, account: 0, txs, ref nonce0, priority: 1);

                    blobTxs.Transactions = txs;
                    blobTxs.ExpectedSelectedTransactions.AddRange(blobTxs.Transactions.Skip(1).Take(2));

                    yield return new TestCaseData(blobTxs).SetName("Blob Transaction Ordering, Multiple Accounts, Nonce Order 7b");
                }

                static ProperTransactionsSelectedTestCase CreateTestCase()
                {
                    var higherPriorityTransactionsSelected = ProperTransactionsSelectedTestCase.Eip1559Default;
                    var accounts = higherPriorityTransactionsSelected.AccountStates;
                    accounts[TestItem.AddressA] = (1000000, 0);
                    accounts[TestItem.AddressB] = (1000000, 0);
                    higherPriorityTransactionsSelected.ReleaseSpec = Cancun.Instance;
                    higherPriorityTransactionsSelected.BaseFee = 1;
                    return higherPriorityTransactionsSelected;
                }

                void AddTxs(int txCount, int blobsPerTx, int account, List<Transaction> txs, ref UInt256 nonce, int priority = -1)
                {
                    var eoa = Accounts[account];
                    for (int i = 0; i < txCount; i++)
                    {
                        txs.Add(CreateBlobTransaction(eoa.address, eoa.key, maxFee: 1000, blobsPerTx, nonce,
                            priority: priority < 0 ? (uint)(blobsPerTx * 2) : (uint)priority));
                        nonce++;
                    }
                }
            }
        }

        [TestCaseSource(nameof(ProperTransactionsSelectedTestCases))]
        [TestCaseSource(nameof(Eip1559LegacyTransactionTestCases))]
        [TestCaseSource(nameof(Eip1559TestCases))]
        [TestCaseSource(nameof(EnoughShardBlobTransactionsSelectedTestCases))]
        [TestCaseSource(nameof(BlobTransactionOrderingTestCases))]
        public void Proper_transactions_selected(ProperTransactionsSelectedTestCase testCase)
        {
            MemDb stateDb = new();
            MemDb codeDb = new();
            TrieStore trieStore = TestTrieStoreFactory.Build(stateDb, LimboLogs.Instance);
            IWorldState stateProvider = new WorldState(trieStore, codeDb, LimboLogs.Instance);
            StateReader _ = new(TestTrieStoreFactory.Build(stateDb, LimboLogs.Instance), codeDb, LimboLogs.Instance);
            ISpecProvider specProvider = Substitute.For<ISpecProvider>();

            void SetAccountStates(IEnumerable<Address> missingAddresses)
            {
                HashSet<Address> missingAddressesSet = missingAddresses.ToHashSet();

                foreach (KeyValuePair<Address, (UInt256 Balance, UInt256 Nonce)> accountState in testCase.AccountStates
                             .Where(v => !missingAddressesSet.Contains(v.Key)))
                {
                    stateProvider.CreateAccount(accountState.Key, accountState.Value.Balance);
                    for (int i = 0; i < accountState.Value.Nonce; i++)
                    {
                        stateProvider.IncrementNonce(accountState.Key);
                    }
                }

                stateProvider.Commit(Homestead.Instance);
                stateProvider.CommitTree(0);
            }

            ITxPool transactionPool = Substitute.For<ITxPool>();
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            Block block = Build.A.Block.WithNumber(0).TestObject;
            blockTree.Head.Returns(block);
            IReleaseSpec spec = testCase.ReleaseSpec;
            specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(spec);
            TransactionComparerProvider transactionComparerProvider =
                new(specProvider, blockTree);
            IComparer<Transaction> defaultComparer = transactionComparerProvider.GetDefaultComparer();
            IComparer<Transaction> comparer = CompareTxByNonce.Instance.ThenBy(defaultComparer);

            Dictionary<AddressAsKey, Transaction[]> GroupTransactions(bool supportBlobs) =>
                testCase.Transactions
                    .Where(t => t.SenderAddress is not null)
                    .Where(t => t.SupportsBlobs == supportBlobs)
                    .GroupBy(t => t.SenderAddress)
                    .ToDictionary(
                        g => new AddressAsKey(g.Key!),
                        g => g.OrderBy(t => t, comparer).ToArray());

            Dictionary<AddressAsKey, Transaction[]> transactions = GroupTransactions(false);
            Dictionary<AddressAsKey, Transaction[]> blobTransactions = GroupTransactions(true);
            transactionPool.GetPendingTransactionsBySender().Returns(transactions);
            transactionPool.GetPendingLightBlobTransactionsBySender().Returns(blobTransactions);
            foreach (Transaction blobTx in blobTransactions.SelectMany(kvp => kvp.Value))
            {
                transactionPool.TryGetPendingBlobTransaction(Arg.Is<Hash256>(h => h == blobTx.Hash),
                    out Arg.Any<Transaction?>()).Returns(x =>
                {
                    x[1] = blobTx;
                    return true;
                });
            }

            BlocksConfig blocksConfig = new() { MinGasPrice = testCase.MinGasPriceForMining };
            ITxFilterPipeline txFilterPipeline = new TxFilterPipelineBuilder(LimboLogs.Instance)
                .WithMinGasPriceFilter(blocksConfig, specProvider)
                .WithBaseFeeFilter(specProvider)
                .Build;

            SetAccountStates(testCase.MissingAddresses);

            TxPoolTxSource poolTxSource = new(transactionPool, specProvider,
                transactionComparerProvider, LimboLogs.Instance, txFilterPipeline);

            BlockHeaderBuilder parentHeader = Build.A.BlockHeader.WithStateRoot(stateProvider.StateRoot).WithBaseFee(testCase.BaseFee);
            if (spec.IsEip4844Enabled)
            {
                parentHeader = parentHeader.WithExcessBlobGas(0);
            }

            IEnumerable<Transaction> selectedTransactions =
                poolTxSource.GetTransactions(parentHeader.TestObject,
                    testCase.GasLimit);
            selectedTransactions.Should()
                .BeEquivalentTo(testCase.ExpectedSelectedTransactions, o => o.WithStrictOrdering());
        }

        public class ProperTransactionsSelectedTestCase
        {
            public IDictionary<Address, (UInt256 Balance, UInt256 Nonce)> AccountStates { get; } =
                new Dictionary<Address, (UInt256 Balance, UInt256 Nonce)>();

            public List<Transaction> Transactions { get; set; } = new();
            public long GasLimit { get; set; }
            public List<Transaction> ExpectedSelectedTransactions { get; } = new();
            public UInt256 MinGasPriceForMining { get; set; } = 1;

            public required IReleaseSpec ReleaseSpec { get; set; }

            public UInt256 BaseFee { get; set; }

            public static ProperTransactionsSelectedTestCase Default =>
                new()
                {
                    AccountStates = { { TestItem.AddressA, (1000, 1) } },
                    Transactions =
                    {
                        Build.A.Transaction.WithSenderAddress(TestItem.AddressA).WithNonce(3).WithValue(1)
                            .WithGasPrice(10).WithGasLimit(10).SignedAndResolved(TestItem.PrivateKeyA).TestObject,
                        Build.A.Transaction.WithSenderAddress(TestItem.AddressA).WithNonce(1).WithValue(10)
                            .WithGasPrice(10).WithGasLimit(10).SignedAndResolved(TestItem.PrivateKeyA).TestObject,
                        Build.A.Transaction.WithSenderAddress(TestItem.AddressA).WithNonce(2).WithValue(10)
                            .WithGasPrice(10).WithGasLimit(10).SignedAndResolved(TestItem.PrivateKeyA).TestObject
                    },
                    GasLimit = 10000000,
                    ReleaseSpec = Berlin.Instance
                };

            public static ProperTransactionsSelectedTestCase Eip1559DefaultLegacyTransactions =>
                new()
                {
                    ReleaseSpec = London.Instance,
                    BaseFee = 1.GWei(),
                    AccountStates = { { TestItem.AddressA, (1000, 1) } },
                    Transactions =
                    {
                        Build.A.Transaction.WithSenderAddress(TestItem.AddressA).WithNonce(3).WithValue(1)
                            .WithGasPrice(10).WithGasLimit(10).SignedAndResolved(TestItem.PrivateKeyA).TestObject,
                        Build.A.Transaction.WithSenderAddress(TestItem.AddressA).WithNonce(1).WithValue(10)
                            .WithGasPrice(10).WithGasLimit(10).SignedAndResolved(TestItem.PrivateKeyA).TestObject,
                        Build.A.Transaction.WithSenderAddress(TestItem.AddressA).WithNonce(2).WithValue(10)
                            .WithGasPrice(10).WithGasLimit(10).SignedAndResolved(TestItem.PrivateKeyA).TestObject
                    },
                    GasLimit = 10000000
                };

            public static ProperTransactionsSelectedTestCase Eip1559Default =>
                new()
                {
                    ReleaseSpec = London.Instance,
                    BaseFee = 1.GWei(),
                    AccountStates = { { TestItem.AddressA, (1000, 1) } },
                    Transactions =
                    {
                        Build.A.Transaction.WithSenderAddress(TestItem.AddressA).WithType(TxType.EIP1559).WithNonce(3).WithValue(1)
                            .WithMaxFeePerGas(10).WithGasLimit(10).SignedAndResolved(TestItem.PrivateKeyA).TestObject,
                        Build.A.Transaction.WithSenderAddress(TestItem.AddressA).WithType(TxType.EIP1559).WithNonce(1).WithValue(10)
                            .WithMaxFeePerGas(10).WithGasLimit(10).SignedAndResolved(TestItem.PrivateKeyA).TestObject,
                        Build.A.Transaction.WithSenderAddress(TestItem.AddressA).WithType(TxType.EIP1559).WithNonce(2).WithValue(10)
                            .WithMaxFeePerGas(10).WithGasLimit(10).SignedAndResolved(TestItem.PrivateKeyA).TestObject
                    },
                    GasLimit = 10000000
                };

            public List<Address> MissingAddresses { get; } = new();
        }
    }
}
