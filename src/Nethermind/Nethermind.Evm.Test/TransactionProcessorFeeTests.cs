// Copyright 2022 Demerzel Solutions Limited
// Licensed under the LGPL-3.0. For full terms, see LICENSE-LGPL in the project root.

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class TransactionProcessorFeeTests
{
    private TestSpecProvider _specProvider;
    private IEthereumEcdsa _ethereumEcdsa;
    private TransactionProcessor _transactionProcessor;
    private IStateProvider _stateProvider;

    [SetUp]
    public void Setup()
    {
        OverridableReleaseSpec spec = new(London.Instance);
        spec.Eip1559FeeCollector = TestItem.AddressC;
        _specProvider = new TestSpecProvider(spec);

        TrieStore trieStore = new(new MemDb(), LimboLogs.Instance);

        _stateProvider = new StateProvider(trieStore, new MemDb(), LimboLogs.Instance);
        _stateProvider.CreateAccount(TestItem.AddressA, 1.Ether());
        _stateProvider.Commit(_specProvider.GenesisSpec);
        _stateProvider.CommitTree(0);

        StorageProvider storageProvider = new(trieStore, _stateProvider, LimboLogs.Instance);
        VirtualMachine virtualMachine = new(TestBlockhashProvider.Instance, _specProvider, LimboLogs.Instance);
        _transactionProcessor = new TransactionProcessor(_specProvider, _stateProvider, storageProvider, virtualMachine,
            LimboLogs.Instance);
        _ethereumEcdsa = new EthereumEcdsa(_specProvider.ChainId, LimboLogs.Instance);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Check_paid_fees_simple(bool isTransactionEip1559)
    {
        OverridableReleaseSpec spec = new(London.Instance);
        spec.Eip1559FeeCollector = TestItem.AddressC;
        _specProvider = new TestSpecProvider(spec);

        Transaction tx = Build.A.Transaction
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).WithGasPrice(10).WithMaxFeePerGas(10)
            .WithType(isTransactionEip1559 ? TxType.EIP1559 : TxType.Legacy).WithGasLimit(21000).TestObject;
        Block block = Build.A.Block.WithNumber(1)
            .WithBeneficiary(TestItem.AddressB).WithBaseFeePerGas(1).WithTransactions(tx).WithGasLimit(21000)
            .TestObject;

        ExecuteAndCheckFees(block, tx);
    }


    [Test]
    public void Check_paid_fees_multiple_transactions()
    {
        Transaction tx1 = Build.A.Transaction
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).WithType(TxType.EIP1559)
            .WithMaxFeePerGas(10).WithGasPrice(1).WithGasLimit(21000).TestObject;
        Transaction tx2 = Build.A.Transaction
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).WithNonce(1)
            .WithGasPrice(10).WithGasLimit(21000).TestObject;
        Block block = Build.A.Block.WithNumber(0).WithBaseFeePerGas(1)
            .WithBeneficiary(TestItem.AddressB).WithTransactions(tx1, tx2).WithGasLimit(42000).TestObject;

        ExecuteAndCheckFees(block, tx1, tx2);
    }


    [Test]
    public void Check_paid_fees_with_byte_code()
    {
        byte[] byteCode = Prepare.EvmCode
            .CallWithValue(Address.Zero, 0, 1)
            .PushData(1)
            .PushData(1)
            .Op(Instruction.SSTORE)
            .PushData(0)
            .PushData(1)
            .Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;
        Transaction tx1 = Build.A.Transaction
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).WithMaxFeePerGas(10).WithGasPrice(1)
            .WithType(TxType.EIP1559).WithGasLimit(21000).TestObject;
        Transaction tx2 = Build.A.Transaction
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).WithNonce(1).WithGasPrice(10)
            .WithType(TxType.Legacy).WithGasLimit(21000).TestObject;
        Transaction tx3 = Build.A.Transaction
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).WithNonce(2).WithMaxFeePerGas(30).WithGasPrice(1)
            .WithType(TxType.EIP1559).WithCode(byteCode)
            .WithGasLimit(60000).TestObject;
        Block block = Build.A.Block.WithNumber(MainnetSpecProvider.LondonBlockNumber)
            .WithBeneficiary(TestItem.AddressB).WithBaseFeePerGas(1).WithTransactions(tx1, tx2, tx3)
            .WithGasLimit(102000).TestObject;

        ExecuteAndCheckFees(block, tx1, tx2, tx3);
    }


    private void ExecuteAndCheckFees(Block block, params Transaction[] txs)
    {
        Address beneficiary = block.Beneficiary!;
        IReleaseSpec spec = _specProvider.GetSpec((block.Number, block.Timestamp));

        BlockReceiptsTracer tracer = new();
        tracer.SetOtherTracer(NullBlockTracer.Instance);

        tracer.StartNewBlockTrace(block);

        UInt256 totalBurned = UInt256.Zero;
        UInt256 totalFees = UInt256.Zero;
        foreach (Transaction tx in txs)
        {
            // Read balances of Eip1559FeeCollector and blockBeneficiary before tx execution
            UInt256 startBurned = _stateProvider.AccountExists(spec.Eip1559FeeCollector!)
                ? _stateProvider.GetBalance(spec.Eip1559FeeCollector!) : 0;
            UInt256 starBeneficiary = _stateProvider.GetBalance(beneficiary);


            tracer.StartNewTxTrace(tx);
            _transactionProcessor.Execute(tx, block.Header, tracer);
            tracer.EndTxTrace();

            // Read balances of Eip1559FeeCollector and blockBeneficiary after tx execution
            UInt256 endBurned = spec.IsEip1559Enabled ? _stateProvider.GetBalance(spec.Eip1559FeeCollector!) : 0;
            UInt256 endBeneficiary = _stateProvider.GetBalance(beneficiary);

            // Calculate expected fees
            UInt256 fees = endBeneficiary - starBeneficiary;
            UInt256 burned = endBurned - startBurned;

            totalFees += fees;
            totalBurned += burned;

            fees.Should().NotBe(0);
            tracer.Fees.Should().Be(totalFees);

            burned.Should().NotBe(0);
            tracer.BurntFees.Should().Be(totalBurned);
        }

        tracer.EndBlockTrace();
    }
}
