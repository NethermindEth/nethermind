// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Encoding;
using Nethermind.Evm.Precompiles;
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

    /// <summary>The zero-topic, zero-data log emitted by <c>Prepare.EvmCode.Log(0, 0)</c> from <paramref name="contract"/>.</summary>
    private static LogEntry ContractLog(Address contract) => new(contract, [], []);

    /// <summary>
    /// Deploys <paramref name="runtime"/> from <see cref="TestItem.AddressA"/> endowed with
    /// <paramref name="endowment"/>, then calls it with <paramref name="callValue"/> in the next block.
    /// </summary>
    /// <returns>The block holding the call transaction, and the deployed contract's address.</returns>
    /// <remarks>
    /// The contract address is derived before either block is added, so a runtime that has to reference
    /// its own address can recompute it from the same head nonce before calling this.
    /// </remarks>
    private static async Task<(Block Block, Address Contract)> DeployAndCall(
        BasicTestBlockchain chain, byte[] runtime, UInt256 endowment, UInt256 callValue, ulong gasLimit = 1_000_000)
    {
        ulong nonce = chain.StateReader.GetNonce(chain.BlockTree.Head!.Header, TestItem.AddressA);
        Address contract = ContractAddress.From(TestItem.AddressA, nonce);

        await chain.AddBlock(Build.A.Transaction
            .WithCode(Prepare.EvmCode.ForInitOf(runtime).Done)
            .WithValue(endowment)
            .WithNonce(nonce)
            .WithGasLimit(gasLimit)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject);

        Block block = await chain.AddBlock(Build.A.Transaction
            .WithTo(contract)
            .WithValue(callValue)
            .WithNonce(nonce + 1)
            .WithGasLimit(gasLimit)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject);

        return (block, contract);
    }

    private void AssertLogs(TxReceipt[] receipts, LogEntry[] expectedLogs, bool logCondition = true)
    {
        LogEntry[][] expected = [eip7708Enabled && logCondition ? expectedLogs : []];
        Assert.That(receipts, Has.Length.EqualTo(expected.Length));

        for (int i = 0; i < expected.Length; i++)
        {
            (receipts[i].Logs ?? []).AssertEquivalentTo(expected[i]);
        }
    }

    [TestCase(1_000_000ul, 1, TestName = "transfer value > 0")]
    [TestCase(1ul, 1, TestName = "transfer value = 1")]
    [TestCase(0ul, 0, TestName = "transfer value = 0")]
    public async Task SimpleTransfer_EmitsLogs(ulong transferValue, int expectedLogCountWhenEnabled)
    {
        using BasicTestBlockchain chain = await CreateChain();

        ulong nonce = chain.StateReader.GetNonce(chain.BlockTree.Head!.Header, TestItem.AddressA);

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
        using BasicTestBlockchain chain = await CreateChain();

        // Contract that calls another address with value, then logs: the trailing LOG0 pins the
        // nested transfer log's position, which has to precede the contract's own log.
        Address targetAddress = TestItem.AddressC;
        byte[] contractCode = Prepare.EvmCode
            .CallWithValue(targetAddress, 100000, innerValue)
            .Log(0, 0)
            .STOP()
            .Done;

        (Block block, Address contractAddress) = await DeployAndCall(chain, contractCode, 10.Ether, 0);

        (chain.ReceiptStorage.Get(block)[0].Logs ?? []).AssertEquivalentTo(eip7708Enabled && innerValue != 0
            ? [ExpectedTransferLog(contractAddress, targetAddress, innerValue), ContractLog(contractAddress)]
            : [ContractLog(contractAddress)]);
    }

    [TestCase(1_000_000ul, 1, TestName = "selfdestruct to other")]
    [TestCase(0ul, 0, TestName = "selfdestruct zero balance")]
    public async Task SelfDestruct_ToDifferentAccount_EmitsTransferLog(ulong contractBalance, int expectedLogCountWhenEnabled)
    {
        using BasicTestBlockchain chain = await CreateChain();

        ulong senderNonce = chain.StateReader.GetNonce(chain.BlockTree.Head!.Header, TestItem.AddressA);

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

    [TestCase(1_000_000ul, TestName = "selfdestruct to self")]
    [TestCase(0ul, TestName = "selfdestruct to self zero balance")]
    public async Task SelfDestruct_ToSelf_NoOp_EmitsNoLog(ulong contractBalance)
    {
        // Post-EIP-6780: selfdestruct to self when contract was NOT created in the same tx
        // is a complete no-op — no destruction, no ETH movement, no log.
        using BasicTestBlockchain chain = await CreateChain();

        ulong senderNonce = chain.StateReader.GetNonce(chain.BlockTree.Head!.Header, TestItem.AddressA);

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

        // No-op selfdestruct should emit no logs — no ETH moves
        AssertLogs(chain.ReceiptStorage.Get(block), []);
    }

    [Test]
    public async Task SelfDestruct_ThenReceivesEth_EmitsLogs()
    {
        using BasicTestBlockchain chain = await CreateChain();

        ulong senderNonce = chain.StateReader.GetNonce(chain.BlockTree.Head!.Header, TestItem.AddressA);

        // Contract A: self-destructs to inheritor only when called with zero value.
        // When called with value, it just accepts the ETH without self-destructing again.
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
            .CallWithValue(contractAAddress, 100000, ethToSend) // Send ETH to self-destructed contract
            .STOP()
            .Done;
        byte[] initCodeB = Prepare.EvmCode
            .ForInitOf(contractBCode)
            .Done;

        // Deploy Contract B with enough ETH
        Transaction deployBTx = Build.A.Transaction
            .WithCode(initCodeB)
            .WithValue(10.Ether)
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

    [Test]
    public async Task TxTransfer_Precedes_ContractLog_And_Bloom_Includes_Both()
    {
        using BasicTestBlockchain chain = await CreateChain();

        byte[] runtime = Prepare.EvmCode.Log(0, 0).STOP().Done;
        (Block block, Address created) = await DeployAndCall(chain, runtime, 1.Ether, 42);

        TxReceipt receipt = chain.ReceiptStorage.Get(block)[0];
        (receipt.Logs ?? []).AssertEquivalentTo(eip7708Enabled
            ? [ExpectedTransferLog(TestItem.AddressA, created, 42), ContractLog(created)]
            : [ContractLog(created)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipt.Bloom!.Matches(Bloom.GetExtract(TransferLog.Sender)), Is.EqualTo(eip7708Enabled), "receipt bloom, transfer log sender");
            Assert.That(receipt.Bloom.Matches(Bloom.GetExtract(TransferLog.TransferSignature)), Is.EqualTo(eip7708Enabled), "receipt bloom, transfer signature");
            Assert.That(receipt.Bloom.Matches(Bloom.GetExtract(created)), Is.True, "receipt bloom, contract log address");
            Assert.That(block.Header.Bloom!.Matches(Bloom.GetExtract(TransferLog.Sender)), Is.EqualTo(eip7708Enabled), "header bloom, transfer log sender");
            Assert.That(block.Header.Bloom.Matches(Bloom.GetExtract(TransferLog.TransferSignature)), Is.EqualTo(eip7708Enabled), "header bloom, transfer signature");
        }
    }

    [Test]
    public async Task SameTx_SelfDestruct_ToSelf_Emits_OpcodeTime_SelfDestructLog()
    {
        // Inline path (7708 without 8037): same-tx self-burn emits the legacy SelfDestruct log at opcode time.
        // Creation and SELFDESTRUCT must happen in the SAME transaction (EIP-6780).
        using BasicTestBlockchain chain = await CreateChain();
        ulong nonce = chain.StateReader.GetNonce(chain.BlockTree.Head!.Header, TestItem.AddressA);

        byte[] selfDestructRuntime = Prepare.EvmCode.Op(Instruction.ADDRESS).Op(Instruction.SELFDESTRUCT).Done;
        byte[] selfDestructInit = Prepare.EvmCode.ForInitOf(selfDestructRuntime).Done;
        const ulong endowment = 1_000_000;

        // The factory's code has to name the child, so both addresses are derived up front. Contract nonces
        // start at 1 after EIP-161, and the factory's first CREATE is the one in callTx.
        Address factory = ContractAddress.From(TestItem.AddressA, nonce);
        Address child = ContractAddress.From(factory, 1);

        byte[] factoryCode = Prepare.EvmCode
            .Create(selfDestructInit, endowment)
            .Call(child, 500_000)
            .Log(0, 0)
            .STOP()
            .Done;

        (Block block, _) = await DeployAndCall(chain, factoryCode, 10.Ether, 0, gasLimit: 2_000_000);

        // SELFDESTRUCT zeroes the balance as it logs, so the end-of-tx destroy pass sees zero and adds nothing:
        // an end-of-tx-only implementation would place the self-destruct log after the factory's log, not before.
        (chain.ReceiptStorage.Get(block)[0].Logs ?? []).AssertEquivalentTo(eip7708Enabled
            ? [ExpectedTransferLog(factory, child, endowment), ExpectedSelfDestructLog(child, endowment), ContractLog(factory)]
            : [ContractLog(factory)]);
    }

    [Test]
    public async Task FailedPrecompileCall_Rolls_Back_Value_And_TransferLog()
    {
        // ecrecover costs 3000; the child gets 0 gas + 2300 CALL stipend, so it OOGs inside the precompile.
        // Both the balance move and the transfer log must roll back with the child frame.
        using BasicTestBlockchain chain = await CreateChain();

        byte[] runtime = Prepare.EvmCode
            .CallWithValue(ECRecoverPrecompile.Address, 0, 5)
            .Log(0, 0)
            .STOP()
            .Done;
        (Block block, Address created) = await DeployAndCall(chain, runtime, 1.Ether, 0);

        // The trailing log is the only one left: it proves execution ran past the CALL, so the missing
        // transfer log is the child frame rolling back rather than the call site never being reached.
        (chain.ReceiptStorage.Get(block)[0].Logs ?? []).AssertEquivalentTo([ContractLog(created)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.StateReader.GetBalance(block.Header, created), Is.EqualTo((UInt256)1.Ether), "caller balance");
            Assert.That(chain.StateReader.GetBalance(block.Header, ECRecoverPrecompile.Address), Is.EqualTo(UInt256.Zero), "precompile balance");
        }
    }
}
