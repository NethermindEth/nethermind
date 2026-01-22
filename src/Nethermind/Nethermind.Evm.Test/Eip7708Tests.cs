// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

[TestFixture(true)]
[TestFixture(false)]
[Parallelizable(ParallelScope.All)]
public class Eip7708Tests(bool eip7708Enabled)
{
    private Task<BasicTestBlockchain> CreateChain()
    {
        OverridableReleaseSpec spec = new(Prague.Instance) { IsEip7708Enabled = eip7708Enabled };
        return BasicTestBlockchain.Create(b => b.AddSingleton<ISpecProvider>(new TestSpecProvider(spec)));
    }

    [TestCase(1_000_000ul, 1, TestName = "transfer value > 0")]
    [TestCase(1ul, 1, TestName = "transfer value = 1")]
    [TestCase(0ul, 0, TestName = "transfer value = 0")]
    public async Task SimpleTransfer_EmitsLogs(ulong transferValue, int expectedLogCountWhenEnabled)
    {
        BasicTestBlockchain chain = await CreateChain();

        UInt256 nonce = chain.StateReader.GetNonce(chain.BlockTree.Head!.Header, TestItem.AddressA);

        Transaction tx = Build.A.Transaction
            .WithTo(TestItem.AddressB)
            .WithValue(transferValue)
            .WithNonce(nonce)
            .WithGasLimit(21000)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        Block block = await chain.AddBlock(tx);
        TxReceipt[] receipts = chain.ReceiptStorage.Get(block);

        int expectedLogCount = eip7708Enabled ? expectedLogCountWhenEnabled : 0;

        Assert.Multiple(() =>
        {
            Assert.That(receipts, Has.Length.EqualTo(1));
            Assert.That(receipts[0].Logs, Has.Length.EqualTo(expectedLogCount));

            if (expectedLogCount > 0)
            {
                LogEntry log = receipts[0].Logs![0];
                Assert.That(log.Address, Is.EqualTo(TransferLog.Sender));
                Assert.That(log.Topics, Has.Length.EqualTo(3));
                Assert.That(log.Topics[0], Is.EqualTo(TransferLog.TransferSignature));
                Assert.That(log.Topics[1], Is.EqualTo(TestItem.AddressA.ToHash().ToHash256()));
                Assert.That(log.Topics[2], Is.EqualTo(TestItem.AddressB.ToHash().ToHash256()));
                Assert.That(new UInt256(log.Data, isBigEndian: true), Is.EqualTo((UInt256)transferValue));
            }
        });
    }

    [TestCase(1_000_000ul, TestName = "subcall with value")]
    [TestCase(0ul, TestName = "subcall with zero inner value")]
    public async Task Subcall_WithValueTransfer_EmitsTransferLogs(ulong innerValue)
    {
        BasicTestBlockchain chain = await CreateChain();

        UInt256 senderNonce = chain.StateReader.GetNonce(chain.BlockTree.Head!.Header, TestItem.AddressA);

        // Contract that calls another address with value
        Address targetAddress = TestItem.AddressC;
        byte[] contractCode = Prepare.EvmCode
            .CallWithValue(targetAddress, 100000, innerValue)
            .STOP()
            .Done;
        byte[] initCode = Prepare.EvmCode
            .ForInitOf(contractCode)
            .Done;

        Address contractAddress = ContractAddress.From(TestItem.AddressA, senderNonce);

        // Deploy the contract with some ETH
        Transaction deployTx = Build.A.Transaction
            .WithCode(initCode)
            .WithValue(10.Ether())
            .WithNonce(senderNonce)
            .WithGasLimit(1_000_000)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        await chain.AddBlock(deployTx);
        senderNonce++;

        // Call the contract to trigger the inner CALL
        Transaction callTx = Build.A.Transaction
            .WithTo(contractAddress)
            .WithValue(0)
            .WithNonce(senderNonce)
            .WithGasLimit(1_000_000)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        Block block = await chain.AddBlock(callTx);
        TxReceipt[] receipts = chain.ReceiptStorage.Get(block);

        Assert.Multiple(() =>
        {
            Assert.That(receipts, Has.Length.EqualTo(1));

            if (eip7708Enabled && innerValue > 0 && receipts[0].Logs?.Length > 0)
            {
                // Inner transfer log (contract -> target)
                LogEntry innerLog = receipts[0].Logs![0];
                Assert.That(innerLog.Address, Is.EqualTo(TransferLog.Sender));
                Assert.That(innerLog.Topics, Has.Length.EqualTo(3));
                Assert.That(innerLog.Topics[0], Is.EqualTo(TransferLog.TransferSignature));
                Assert.That(innerLog.Topics[1], Is.EqualTo(contractAddress.ToHash().ToHash256()));
                Assert.That(innerLog.Topics[2], Is.EqualTo(targetAddress.ToHash().ToHash256()));
                Assert.That(new UInt256(innerLog.Data, isBigEndian: true), Is.EqualTo((UInt256)innerValue));
            }
            else if (!eip7708Enabled || innerValue == 0)
            {
                // No logs expected when EIP-7708 is disabled or value is 0
                Assert.That(receipts[0].Logs, Has.Length.EqualTo(0));
            }
        });
    }

    [TestCase(1_000_000ul, 1, TestName = "selfdestruct to other")]
    [TestCase(0ul, 0, TestName = "selfdestruct zero balance")]
    public async Task SelfDestruct_ToDifferentAccount_EmitsTransferLog(ulong contractBalance, int expectedLogCountWhenEnabled)
    {
        BasicTestBlockchain chain = await CreateChain();

        UInt256 senderNonce = chain.StateReader.GetNonce(chain.BlockTree.Head!.Header, TestItem.AddressA);

        // Contract that self-destructs to a different address (inheritor)
        Address inheritor = TestItem.AddressC;
        byte[] contractCode = Prepare.EvmCode
            .SELFDESTRUCT(inheritor)
            .Done;
        byte[] initCode = Prepare.EvmCode
            .ForInitOf(contractCode)
            .Done;

        Address contractAddress = ContractAddress.From(TestItem.AddressA, senderNonce);

        // Deploy the contract with some ETH
        Transaction deployTx = Build.A.Transaction
            .WithCode(initCode)
            .WithValue(contractBalance)
            .WithNonce(senderNonce)
            .WithGasLimit(1_000_000)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        await chain.AddBlock(deployTx);
        senderNonce++;

        // Call the contract to trigger selfdestruct
        Transaction callTx = Build.A.Transaction
            .WithTo(contractAddress)
            .WithValue(0)
            .WithNonce(senderNonce)
            .WithGasLimit(1_000_000)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        Block block = await chain.AddBlock(callTx);
        TxReceipt[] receipts = chain.ReceiptStorage.Get(block);

        int expectedLogCount = eip7708Enabled ? expectedLogCountWhenEnabled : 0;

        Assert.Multiple(() =>
        {
            Assert.That(receipts, Has.Length.EqualTo(1));
            Assert.That(receipts[0].Logs, Has.Length.EqualTo(expectedLogCount));

            if (expectedLogCount > 0)
            {
                // TransferLog: contract -> inheritor
                LogEntry log = receipts[0].Logs![0];
                Assert.That(log.Address, Is.EqualTo(TransferLog.Sender));
                Assert.That(log.Topics, Has.Length.EqualTo(3));
                Assert.That(log.Topics[0], Is.EqualTo(TransferLog.TransferSignature));
                Assert.That(log.Topics[1], Is.EqualTo(contractAddress.ToHash().ToHash256()));
                Assert.That(log.Topics[2], Is.EqualTo(inheritor.ToHash().ToHash256()));
                Assert.That(new UInt256(log.Data, isBigEndian: true), Is.EqualTo((UInt256)contractBalance));
            }
        });
    }

    [TestCase(1_000_000ul, 1, TestName = "selfdestruct to self")]
    [TestCase(0ul, 0, TestName = "selfdestruct to self zero balance")]
    public async Task SelfDestruct_ToSelf_EmitsSelfDestructLog(ulong contractBalance, int expectedLogCountWhenEnabled)
    {
        BasicTestBlockchain chain = await CreateChain();

        UInt256 senderNonce = chain.StateReader.GetNonce(chain.BlockTree.Head!.Header, TestItem.AddressA);

        // Calculate contract address first - we need it for the selfdestruct target
        Address contractAddress = ContractAddress.From(TestItem.AddressA, senderNonce);

        // Contract that self-destructs to itself
        byte[] contractCode = Prepare.EvmCode
            .SELFDESTRUCT(contractAddress)
            .Done;
        byte[] initCode = Prepare.EvmCode
            .ForInitOf(contractCode)
            .Done;

        // Deploy the contract with some ETH
        Transaction deployTx = Build.A.Transaction
            .WithCode(initCode)
            .WithValue(contractBalance)
            .WithNonce(senderNonce)
            .WithGasLimit(1_000_000)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        await chain.AddBlock(deployTx);
        senderNonce++;

        // Call the contract to trigger selfdestruct to self
        Transaction callTx = Build.A.Transaction
            .WithTo(contractAddress)
            .WithValue(0)
            .WithNonce(senderNonce)
            .WithGasLimit(1_000_000)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        Block block = await chain.AddBlock(callTx);
        TxReceipt[] receipts = chain.ReceiptStorage.Get(block);

        int expectedLogCount = eip7708Enabled ? expectedLogCountWhenEnabled : 0;

        Assert.Multiple(() =>
        {
            Assert.That(receipts, Has.Length.EqualTo(1));
            Assert.That(receipts[0].Logs, Has.Length.EqualTo(expectedLogCount));

            if (expectedLogCount > 0)
            {
                // SelfDestructLog: uses different signature and LOG2 format (only 2 topics)
                LogEntry log = receipts[0].Logs![0];
                Assert.That(log.Address, Is.EqualTo(TransferLog.Sender));
                Assert.That(log.Topics, Has.Length.EqualTo(2));
                Assert.That(log.Topics[0], Is.EqualTo(TransferLog.SelfDestructSignature));
                Assert.That(log.Topics[1], Is.EqualTo(contractAddress.ToHash().ToHash256()));
                Assert.That(new UInt256(log.Data, isBigEndian: true), Is.EqualTo((UInt256)contractBalance));
            }
        });
    }

    [Test]
    public async Task SelfDestruct_ThenReceivesEth_EmitsLogs()
    {
        BasicTestBlockchain chain = await CreateChain();

        UInt256 senderNonce = chain.StateReader.GetNonce(chain.BlockTree.Head!.Header, TestItem.AddressA);

        // Calculate contract A address first
        Address contractAAddress = ContractAddress.From(TestItem.AddressA, senderNonce);

        // Contract A: self-destructs to another address, then Contract B will send it ETH
        Address inheritorA = TestItem.AddressD;
        byte[] contractACode = Prepare.EvmCode
            .SELFDESTRUCT(inheritorA)
            .Done;
        byte[] initCodeA = Prepare.EvmCode
            .ForInitOf(contractACode)
            .Done;

        // Deploy Contract A with some ETH
        ulong contractABalance = 1_000_000;
        Transaction deployATx = Build.A.Transaction
            .WithCode(initCodeA)
            .WithValue(contractABalance)
            .WithNonce(senderNonce)
            .WithGasLimit(1_000_000)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        await chain.AddBlock(deployATx);
        senderNonce++;

        // Contract B: calls Contract A (triggers selfdestruct), then sends ETH to Contract A
        ulong ethToSend = 500_000;
        byte[] contractBCode = Prepare.EvmCode
            .Call(contractAAddress, 100000)             // This triggers Contract A's selfdestruct
            .CallWithValue(contractAAddress, 100000, ethToSend) // Send ETH to self-destructed contract
            .STOP()
            .Done;
        byte[] initCodeB = Prepare.EvmCode
            .ForInitOf(contractBCode)
            .Done;

        Address contractBAddress = ContractAddress.From(TestItem.AddressA, senderNonce);

        // Deploy Contract B with enough ETH to send
        Transaction deployBTx = Build.A.Transaction
            .WithCode(initCodeB)
            .WithValue(10.Ether())
            .WithNonce(senderNonce)
            .WithGasLimit(1_000_000)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        await chain.AddBlock(deployBTx);
        senderNonce++;

        // Call Contract B to trigger the sequence
        Transaction callTx = Build.A.Transaction
            .WithTo(contractBAddress)
            .WithValue(0)
            .WithNonce(senderNonce)
            .WithGasLimit(1_000_000)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        Block block = await chain.AddBlock(callTx);
        TxReceipt[] receipts = chain.ReceiptStorage.Get(block);

        Assert.Multiple(() =>
        {
            Assert.That(receipts, Has.Length.EqualTo(1));

            if (eip7708Enabled)
            {
                // Expected logs:
                // 1. TransferLog from Contract A selfdestruct (Contract A -> inheritorA)
                // 2. TransferLog from Contract B sending ETH to Contract A (Contract B -> Contract A)
                // 3. SelfDestructLog for account closure (Contract A with new balance)
                Assert.That(receipts[0].Logs, Has.Length.EqualTo(3));

                // First log: selfdestruct transfer (Contract A -> inheritorA)
                LogEntry selfDestructTransfer = receipts[0].Logs![0];
                Assert.That(selfDestructTransfer.Address, Is.EqualTo(TransferLog.Sender));
                Assert.That(selfDestructTransfer.Topics[0], Is.EqualTo(TransferLog.TransferSignature));
                Assert.That(selfDestructTransfer.Topics[1], Is.EqualTo(contractAAddress.ToHash().ToHash256()));
                Assert.That(selfDestructTransfer.Topics[2], Is.EqualTo(inheritorA.ToHash().ToHash256()));
                Assert.That(new UInt256(selfDestructTransfer.Data, isBigEndian: true), Is.EqualTo((UInt256)contractABalance));

                // Second log: ETH transfer from B to A
                LogEntry ethTransfer = receipts[0].Logs![1];
                Assert.That(ethTransfer.Address, Is.EqualTo(TransferLog.Sender));
                Assert.That(ethTransfer.Topics[0], Is.EqualTo(TransferLog.TransferSignature));
                Assert.That(ethTransfer.Topics[1], Is.EqualTo(contractBAddress.ToHash().ToHash256()));
                Assert.That(ethTransfer.Topics[2], Is.EqualTo(contractAAddress.ToHash().ToHash256()));
                Assert.That(new UInt256(ethTransfer.Data, isBigEndian: true), Is.EqualTo((UInt256)ethToSend));

                // Third log: account closure - TransferLog sending remaining balance to zero address
                // Per EIP-6780/7708: when account is destroyed, any balance received after SELFDESTRUCT
                // is transferred out at end of transaction
                LogEntry closureLog = receipts[0].Logs![2];
                Assert.That(closureLog.Address, Is.EqualTo(TransferLog.Sender));
                Assert.That(closureLog.Topics[0], Is.EqualTo(TransferLog.TransferSignature));
            }
            else
            {
                Assert.That(receipts[0].Logs, Has.Length.EqualTo(0));
            }
        });
    }
}
