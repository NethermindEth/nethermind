// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using NUnit.Framework;
using System.Collections;
using Nethermind.Core.Test;
using Nethermind.Evm;
using Nethermind.Evm.Test;
using Nethermind.Taiko.TaikoSpec;

namespace Nethermind.Taiko.Test;

public class TransactionProcessorTests
{
    private readonly TaikoOntakeReleaseSpec _spec;
    private readonly ISpecProvider _specProvider;
    private readonly IEthereumEcdsa _ethereumEcdsa;
    private TaikoTransactionProcessor? _transactionProcessor;
    private WorldState? _stateProvider;

    public TransactionProcessorTests()
    {
        _spec = new TaikoOntakeReleaseSpec();
        _specProvider = new TestSpecProvider(_spec);
        _ethereumEcdsa = new EthereumEcdsa(_specProvider.ChainId);
    }

    private static readonly UInt256 AccountBalance = 1.Ether();

    [SetUp]
    public void Setup()
    {
        _spec.FeeCollector = TestItem.AddressB;

        MemDb stateDb = new();
        TrieStore trieStore = TestTrieStoreFactory.Build(stateDb, LimboLogs.Instance);
        _stateProvider = new WorldState(trieStore, new MemDb(), LimboLogs.Instance);
        _stateProvider.CreateAccount(TestItem.AddressA, AccountBalance);
        _stateProvider.Commit(_specProvider.GenesisSpec);
        _stateProvider.CommitTree(0);

        CodeInfoRepository codeInfoRepository = new();
        VirtualMachine virtualMachine = new(new TestBlockhashProvider(_specProvider), _specProvider, LimboLogs.Instance);
        _transactionProcessor = new TaikoTransactionProcessor(_specProvider, _stateProvider, virtualMachine, codeInfoRepository, LimboLogs.Instance);
    }


    [TestCaseSource(nameof(FeesDistributionTests))]
    public void Fees_distributed_correctly(byte basefeeSharingPctg, UInt256 goesToTreasury, UInt256 goesToBeneficiary, ulong gasPrice)
    {
        long gasLimit = 100000;
        Address benefeciaryAddress = TestItem.AddressC;

        Transaction tx = Build.A.Transaction
            .WithValue(1)
            .WithGasPrice(gasPrice)
            .WithGasLimit(gasLimit)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;

        var extraData = new byte[32];
        extraData[31] = basefeeSharingPctg;

        Block block = Build.A.Block.WithNumber(1).WithTransactions(tx)
            .WithBaseFeePerGas(gasPrice)
            .WithExtraData(extraData)
            .WithBeneficiary(benefeciaryAddress).WithGasLimit(gasLimit).TestObject;

        _transactionProcessor!.Execute(tx, new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)), NullTxTracer.Instance);

        Assert.Multiple(() =>
        {
            Assert.That(_stateProvider!.GetBalance(_spec.FeeCollector!), Is.EqualTo(goesToTreasury));
            Assert.That(_stateProvider.GetBalance(benefeciaryAddress), Is.EqualTo(goesToBeneficiary));
        });
    }

    public static IEnumerable FeesDistributionTests
    {
        get
        {
            static object[] Typed(int basefeeSharingPctg, ulong goesToTreasury, ulong goesToBeneficiary, ulong gasPrice)
                => [(byte)basefeeSharingPctg, (UInt256)goesToTreasury, (UInt256)goesToBeneficiary, gasPrice];

            yield return new TestCaseData(Typed(0, 21000, 0, 1)) { TestName = "All goes to treasury" };
            yield return new TestCaseData(Typed(100, 0, 21000, 1)) { TestName = "All goes to beneficiary" };

            yield return new TestCaseData(Typed(50, 10500, 10500, 1)) { TestName = "50/50" };

            yield return new TestCaseData(Typed(75, 5250, 15750, 1)) { TestName = "1/4 to treasury" };
            yield return new TestCaseData(Typed(99, 210, 20790, 1)) { TestName = "Smallest value to treasury" };
            yield return new TestCaseData(Typed(1, 20790, 210, 1)) { TestName = "Smallest value to beneficiary" };

            yield return new TestCaseData(Typed(128, 0, 21000, 1)) { TestName = "Out of borders" };

            yield return new TestCaseData(Typed(11, 18690, 2310, 1)) { TestName = "Prime value #1" };
            yield return new TestCaseData(Typed(7, 19530, 1470, 1)) { TestName = "Prime value #2" };
            yield return new TestCaseData(Typed(97, 630, 20370, 1)) { TestName = "Prime value #3" };

            yield return new TestCaseData(Typed(97, 1890, 61110, 3)) { TestName = "Prime value and price gas #1" };
            yield return new TestCaseData(Typed(97, 69930, 2261070, 111)) { TestName = "Prime value and price gas #2" };
            yield return new TestCaseData(Typed(97, 3843630, 124277370, 6101)) { TestName = "Prime value and price gas #3" };
        }
    }
}
