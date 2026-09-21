// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
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

/// <summary>
/// Receipt-level pins for EIP-7708 behaviors covered only by code inspection so far:
/// log-index interleaving with contract logs, bloom inclusion, same-tx self-burn,
/// and precompile-failure rollback.
/// </summary>
[TestFixture]
public class Eip7708OrderingAndBloomTests
{
    private Task<BasicTestBlockchain> CreateChain()
    {
        OverridableReleaseSpec spec = new(Prague.Instance) { IsEip7708Enabled = true };
        return BasicTestBlockchain.Create(b => b.AddSingleton<ISpecProvider>(new TestSpecProvider(spec)));
    }

    private static LogEntry ExpectedTransferLog(Address from, Address to, UInt256 value) =>
        new(TransferLog.Sender, value.ToBigEndian(), [TransferLog.TransferSignature, from.ToHash().ToHash256(), to.ToHash().ToHash256()]);

    [Test]
    public async Task TxTransfer_Precedes_ContractLog_And_Bloom_Includes_Both()
    {
        BasicTestBlockchain chain = await CreateChain();
        ulong nonce = chain.StateReader.GetNonce(chain.BlockTree.Head!.Header, TestItem.AddressA);

        byte[] runtime = Prepare.EvmCode.Log(0, 0).STOP().Done;
        byte[] init = Prepare.EvmCode.ForInitOf(runtime).Done;
        Address created = ContractAddress.From(TestItem.AddressA, nonce);

        Transaction deployTx = Build.A.Transaction
            .WithCode(init)
            .WithValue(1.Ether)
            .WithNonce(nonce)
            .WithGasLimit(1_000_000)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;
        await chain.AddBlock(deployTx);

        Transaction callTx = Build.A.Transaction
            .WithTo(created)
            .WithValue(42)
            .WithNonce(nonce + 1)
            .WithGasLimit(1_000_000)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;
        Block block = await chain.AddBlock(callTx);

        LogEntry[] logs = chain.ReceiptStorage.Get(block)[0].Logs!;
        LogEntry[] expected =
        [
            ExpectedTransferLog(TestItem.AddressA, created, 42),
            new LogEntry(created, [], []),
        ];
        Assert.That(logs, Has.Length.EqualTo(expected.Length));
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.That(logs[i].Address, Is.EqualTo(expected[i].Address), $"log {i} address");
            Assert.That(logs[i].Topics, Is.EqualTo(expected[i].Topics), $"log {i} topics");
            Assert.That(logs[i].Data, Is.EqualTo(expected[i].Data), $"log {i} data");
        }

        Bloom receiptBloom = chain.ReceiptStorage.Get(block)[0].Bloom;
        Assert.That(receiptBloom.Matches(Bloom.GetExtract(TransferLog.Sender)), Is.True);
        Assert.That(receiptBloom.Matches(Bloom.GetExtract(TransferLog.TransferSignature)), Is.True);
        Assert.That(receiptBloom.Matches(Bloom.GetExtract(created)), Is.True);

        Assert.That(block.Header.Bloom!.Matches(Bloom.GetExtract(TransferLog.TransferSignature)), Is.True);
        Assert.That(block.Header.Bloom.Matches(Bloom.GetExtract(TransferLog.Sender)), Is.True);
    }

    [Test]
    public async Task NestedCallTransfer_Precedes_OuterContractLog()
    {
        BasicTestBlockchain chain = await CreateChain();
        ulong nonce = chain.StateReader.GetNonce(chain.BlockTree.Head!.Header, TestItem.AddressA);

        Address target = TestItem.AddressC;
        byte[] runtime = Prepare.EvmCode
            .CallWithValue(target, 50_000, 7)
            .Log(0, 0)
            .STOP()
            .Done;
        byte[] init = Prepare.EvmCode.ForInitOf(runtime).Done;
        Address created = ContractAddress.From(TestItem.AddressA, nonce);

        Transaction deployTx = Build.A.Transaction
            .WithCode(init)
            .WithValue(1.Ether)
            .WithNonce(nonce)
            .WithGasLimit(1_000_000)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;
        await chain.AddBlock(deployTx);

        Transaction callTx = Build.A.Transaction
            .WithTo(created)
            .WithValue(0)
            .WithNonce(nonce + 1)
            .WithGasLimit(1_000_000)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;
        Block block = await chain.AddBlock(callTx);

        LogEntry[] logs = chain.ReceiptStorage.Get(block)[0].Logs!;
        Assert.That(logs, Has.Length.EqualTo(2));
        Assert.That(logs[0].Address, Is.EqualTo(TransferLog.Sender));
        Assert.That(logs[0].Topics![1], Is.EqualTo(created.ToHash().ToHash256()));
        Assert.That(logs[0].Topics![2], Is.EqualTo(target.ToHash().ToHash256()));
        Assert.That(new UInt256(logs[0].Data, true), Is.EqualTo((UInt256)7));
        Assert.That(logs[1].Address, Is.EqualTo(created));
    }

    [Test]
    public async Task SameTx_SelfDestruct_ToSelf_Emits_OpcodeTime_SelfDestructLog()
    {
        // Inline path (7708 without 8037): same-tx self-burn emits the legacy SelfDestruct log at opcode time.
        // Creation and SELFDESTRUCT must happen in the SAME transaction (EIP-6780).
        BasicTestBlockchain chain = await CreateChain();
        ulong nonce = chain.StateReader.GetNonce(chain.BlockTree.Head!.Header, TestItem.AddressA);

        byte[] selfDestructRuntime = Prepare.EvmCode.Op(Instruction.ADDRESS).Op(Instruction.SELFDESTRUCT).Done;
        byte[] selfDestructInit = Prepare.EvmCode.ForInitOf(selfDestructRuntime).Done;
        const ulong endowment = 1_000_000;

        Address factory = ContractAddress.From(TestItem.AddressA, nonce);
        Address child = ContractAddress.From(factory, 1);

        byte[] factoryCode = Prepare.EvmCode
            .Create(selfDestructInit, endowment)
            .Call(child, 500_000)
            .STOP()
            .Done;
        byte[] factoryInit = Prepare.EvmCode.ForInitOf(factoryCode).Done;

        Transaction deployTx = Build.A.Transaction
            .WithCode(factoryInit)
            .WithValue(10.Ether)
            .WithNonce(nonce)
            .WithGasLimit(2_000_000)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;
        await chain.AddBlock(deployTx);

        Transaction callTx = Build.A.Transaction
            .WithTo(factory)
            .WithValue(0)
            .WithNonce(nonce + 1)
            .WithGasLimit(2_000_000)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;
        Block block = await chain.AddBlock(callTx);

        LogEntry[] logs = chain.ReceiptStorage.Get(block)[0].Logs!;
        Assert.That(logs, Has.Length.EqualTo(2));
        Assert.That(logs[0].Topics![0], Is.EqualTo(TransferLog.TransferSignature));
        Assert.That(logs[0].Topics![1], Is.EqualTo(factory.ToHash().ToHash256()));
        Assert.That(logs[0].Topics![2], Is.EqualTo(child.ToHash().ToHash256()));
        Assert.That(logs[1].Topics![0], Is.EqualTo(TransferLog.SelfDestructSignature));
        Assert.That(logs[1].Topics![1], Is.EqualTo(child.ToHash().ToHash256()));
        Assert.That(new UInt256(logs[1].Data, true), Is.EqualTo((UInt256)endowment));
        Assert.That(chain.StateReader.GetBalance(block.Header, child), Is.EqualTo(UInt256.Zero));
    }

    [Test]
    public async Task FailedPrecompileCall_Rolls_Back_Value_And_TransferLog()
    {
        // ecrecover costs 3000; the child gets 0 gas + 2300 CALL stipend, so it OOGs inside the precompile.
        // Both the balance move and the transfer log must roll back with the child frame.
        BasicTestBlockchain chain = await CreateChain();
        ulong nonce = chain.StateReader.GetNonce(chain.BlockTree.Head!.Header, TestItem.AddressA);

        Address ecrecover = new("0x0000000000000000000000000000000000000001");
        byte[] runtime = Prepare.EvmCode
            .CallWithValue(ecrecover, 0, 5)
            .STOP()
            .Done;
        byte[] init = Prepare.EvmCode.ForInitOf(runtime).Done;
        Address created = ContractAddress.From(TestItem.AddressA, nonce);

        Transaction deployTx = Build.A.Transaction
            .WithCode(init)
            .WithValue(1.Ether)
            .WithNonce(nonce)
            .WithGasLimit(1_000_000)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;
        await chain.AddBlock(deployTx);

        Transaction callTx = Build.A.Transaction
            .WithTo(created)
            .WithValue(0)
            .WithNonce(nonce + 1)
            .WithGasLimit(1_000_000)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;
        Block block = await chain.AddBlock(callTx);

        Assert.That(chain.ReceiptStorage.Get(block)[0].Logs!, Is.Empty);
        Assert.That(chain.StateReader.GetBalance(block.Header, created), Is.EqualTo((UInt256)1.Ether));
        Assert.That(chain.StateReader.GetBalance(block.Header, ecrecover), Is.EqualTo(UInt256.Zero));
    }
}
