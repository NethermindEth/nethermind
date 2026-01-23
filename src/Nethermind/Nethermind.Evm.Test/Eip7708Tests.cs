// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
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

    private static LogEntry ExpectedTransferLog(Address from, Address to, UInt256 value) =>
        new(TransferLog.Sender, value.ToBigEndian(), [TransferLog.TransferSignature, from.ToHash().ToHash256(), to.ToHash().ToHash256()]);

    private static LogEntry ExpectedSelfDestructLog(Address account, UInt256 value) =>
        new(TransferLog.Sender, value.ToBigEndian(), [TransferLog.SelfDestructSignature, account.ToHash().ToHash256()]);

    private void AssertLogs(TxReceipt[] receipts, LogEntry[] expectedLogs, bool logCondition = true)
    {
        LogEntry[][] expected = [eip7708Enabled && logCondition ? expectedLogs : []];
        receipts.Select(r => r.Logs).Should().BeEquivalentTo(expected);
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

        AssertLogs(chain.ReceiptStorage.Get(block), [ExpectedTransferLog(TestItem.AddressA, TestItem.AddressB, transferValue)], transferValue != 0);
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

        AssertLogs(chain.ReceiptStorage.Get(block), [ExpectedTransferLog(contractAddress, targetAddress, innerValue)], logCondition: innerValue != 0);
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

        AssertLogs(chain.ReceiptStorage.Get(block), [ExpectedTransferLog(contractAddress, inheritor, contractBalance)], contractBalance != 0);
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

        AssertLogs(chain.ReceiptStorage.Get(block), [ExpectedSelfDestructLog(contractAddress, contractBalance)], contractBalance != 0);
    }

    [Test]
    public async Task SelfDestruct_ThenReceivesEth_EmitsLogs()
    {
        BasicTestBlockchain chain = await CreateChain();

        UInt256 senderNonce = chain.StateReader.GetNonce(chain.BlockTree.Head!.Header, TestItem.AddressA);

        // Contract A: self-destructs to inheritor only when called with zero value.
        // When called with value, it just accepts the ETH without selfdestructing again.
        Address inheritorA = TestItem.AddressD;
        byte[] contractACode = Prepare.EvmCode
            .CALLVALUE()        // Get call value
            .Op(Instruction.ISZERO)  // Check if zero
            .PushData(6)        // Jump destination (SELFDESTRUCT starts at byte 6)
            .JUMPI()            // Jump if value is zero
            .STOP()             // If value > 0, just stop
            .JUMPDEST()
            .SELFDESTRUCT(inheritorA)
            .Done;
        byte[] initCodeA = Prepare.EvmCode
            .ForInitOf(contractACode)
            .Done;

        ulong contractABalance = 1_000_000;
        ulong ethToSend = 500_000;

        // Contract B creates Contract A in the same transaction, calls it (triggers selfdestruct),
        // then sends more ETH to it. Under EIP-6780, Contract A will be destroyed at end of tx
        // because it was created in the same transaction.
        // Contract A address = CREATE from Contract B with nonce 1 (contract nonces start at 1 after EIP-161)
        Address contractBAddress = ContractAddress.From(TestItem.AddressA, senderNonce);
        Address contractAAddress = ContractAddress.From(contractBAddress, 1);

        byte[] contractBCode = Prepare.EvmCode
            .Create(initCodeA, contractABalance)        // Create Contract A with initial balance
            .Call(contractAAddress, 100000)             // Call Contract A (triggers selfdestruct)
            .CallWithValue(contractAAddress, 100000, ethToSend) // Send ETH to selfdestructed contract
            .STOP()
            .Done;
        byte[] initCodeB = Prepare.EvmCode
            .ForInitOf(contractBCode)
            .Done;

        // Deploy Contract B with enough ETH
        Transaction deployBTx = Build.A.Transaction
            .WithCode(initCodeB)
            .WithValue(10.Ether())
            .WithNonce(senderNonce)
            .WithGasLimit(2_000_000)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        await chain.AddBlock(deployBTx);
        senderNonce++;

        // Call Contract B to trigger the sequence
        Transaction callTx = Build.A.Transaction
            .WithTo(contractBAddress)
            .WithValue(0)
            .WithNonce(senderNonce)
            .WithGasLimit(2_000_000)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        Block block = await chain.AddBlock(callTx);

        // Expected logs:
        // 1. TransferLog from CREATE (Contract B -> Contract A with initial balance)
        // 2. TransferLog from Contract A selfdestruct (Contract A -> inheritorA)
        // 3. TransferLog from Contract B sending ETH to Contract A (Contract B -> Contract A)
        // 4. SelfDestructLog for account closure - from TransactionProcessor.cs when destroying
        //    accounts in DestroyList at end of transaction (EIP-6780: created in same tx)
        AssertLogs(chain.ReceiptStorage.Get(block), [
            ExpectedTransferLog(contractBAddress, contractAAddress, contractABalance),
            ExpectedTransferLog(contractAAddress, inheritorA, contractABalance),
            ExpectedTransferLog(contractBAddress, contractAAddress, ethToSend),
            ExpectedSelfDestructLog(contractAAddress, ethToSend)
        ]);
    }
}
