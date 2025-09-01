// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;

//move all to correct folder
namespace Nethermind.Evm.Test;

[TestFixture]
public class BlockAccessListTests()
{
    private static readonly OverridableReleaseSpec _spec = new(Prague.Instance)
    {
        IsEip7928Enabled = true
    };

    private static readonly ISpecProvider _specProvider = new TestSpecProvider(_spec);
    private static readonly UInt256 _accountBalance = 10.Ether();

    [Test]
    public void Empty_account_changes()
    {
        Block block = Build.A.Block.WithTransactions(Build.A.Transaction.TestObject).TestObject;

        BlockAccessTracer tracer = new();
        tracer.StartNewBlockTrace(block);
        tracer.StartNewTxTrace(block.Transactions[0]);
        tracer.MarkAsSuccess(TestItem.AddressA, 100, [], [], TestItem.KeccakF);

        Assert.That(tracer.BlockAccessList.AccountChanges, Has.Count.EqualTo(0));
    }

    // [Test]
    // public void Balance_and_nonce_changes()
    // {
    //     ulong gasPrice = 2;
    //     long gasLimit = 100000;
    //     Transaction tx = Build.A.Transaction
    //         .WithTo(TestItem.AddressB)
    //         .WithSenderAddress(TestItem.AddressA)
    //         .WithValue(0)
    //         .WithGasPrice(gasPrice)
    //         .WithGasLimit(gasLimit)
    //         .TestObject;

    //     Block block = Build.A.Block
    //         .WithTransactions(tx)
    //         .WithBaseFeePerGas(1)
    //         .WithBeneficiary(TestItem.AddressC).TestObject;

    //     // BlockReceiptsTracer blockReceiptsTracer = new();
    //     // BlockAccessTracer accessTracer = new();
    //     // blockReceiptsTracer.SetOtherTracer(accessTracer);
    //     // Execute(tx, block, blockReceiptsTracer);

    //     SortedDictionary<Address, AccountChanges> accountChanges = accessTracer.BlockAccessList.AccountChanges;
    //     Assert.That(accountChanges, Has.Count.EqualTo(3));

    //     List<BalanceChange> senderBalanceChanges = accountChanges[TestItem.AddressA].BalanceChanges;
    //     List<NonceChange> senderNonceChanges = accountChanges[TestItem.AddressA].NonceChanges;
    //     List<BalanceChange> toBalanceChanges = accountChanges[TestItem.AddressB].BalanceChanges;
    //     List<BalanceChange> beneficiaryBalanceChanges = accountChanges[TestItem.AddressC].BalanceChanges;

    //     using (Assert.EnterMultipleScope())
    //     {
    //         Assert.That(senderBalanceChanges, Has.Count.EqualTo(1));
    //         // Assert.That(senderBalanceChanges[0].PostBalance, Is.EqualTo(AccountBalance - gasPrice * GasCostOf.Transaction));

    //         Assert.That(senderNonceChanges, Has.Count.EqualTo(1));
    //         Assert.That(senderNonceChanges[0].NewNonce, Is.EqualTo(1));

    //         // zero balance change should not be recorded
    //         Assert.That(toBalanceChanges, Is.Empty);

    //         Assert.That(beneficiaryBalanceChanges, Has.Count.EqualTo(1));
    //         Assert.That(beneficiaryBalanceChanges[0].PostBalance, Is.EqualTo(new UInt256(GasCostOf.Transaction)));
    //     }
    // }

    [Test]
    public void Can_encode_and_decode()
    {
        StorageChange storageChange = new()
        {
            BlockAccessIndex = 10,
            NewValue = new([.. Enumerable.Repeat<byte>(50, 32)])
        };
        byte[] storageChangeBytes = Rlp.Encode(storageChange, RlpBehaviors.None).Bytes;
        StorageChange storageChangeDecoded = Rlp.Decode<StorageChange>(storageChangeBytes, RlpBehaviors.None);
        Assert.That(storageChange, Is.EqualTo(storageChangeDecoded));

        SlotChanges slotChanges = new()
        {
            Slot = [.. Enumerable.Repeat<byte>(100, 32)],
            Changes = [storageChange, storageChange]
        };
        byte[] slotChangesBytes = Rlp.Encode(slotChanges, RlpBehaviors.None).Bytes;
        SlotChanges slotChangesDecoded = Rlp.Decode<SlotChanges>(slotChangesBytes, RlpBehaviors.None);
        Assert.That(slotChanges, Is.EqualTo(slotChangesDecoded));

        StorageRead storageRead = new(new Bytes32([.. Enumerable.Repeat<byte>(50, 32)]));
        byte[] storageReadBytes = Rlp.Encode(storageRead, RlpBehaviors.None).Bytes;
        StorageRead storageReadDecoded = Rlp.Decode<StorageRead>(storageReadBytes, RlpBehaviors.None);
        Assert.That(storageRead, Is.EqualTo(storageReadDecoded));

        BalanceChange balanceChange = new()
        {
            BlockAccessIndex = 10,
            PostBalance = 0
        };
        byte[] balanceChangeBytes = Rlp.Encode(balanceChange, RlpBehaviors.None).Bytes;
        BalanceChange balanceChangeDecoded = Rlp.Decode<BalanceChange>(balanceChangeBytes, RlpBehaviors.None);
        Assert.That(balanceChange, Is.EqualTo(balanceChangeDecoded));

        NonceChange nonceChange = new()
        {
            BlockAccessIndex = 10,
            NewNonce = 0
        };
        byte[] nonceChangeBytes = Rlp.Encode(nonceChange, RlpBehaviors.None).Bytes;
        NonceChange nonceChangeDecoded = Rlp.Decode<NonceChange>(nonceChangeBytes, RlpBehaviors.None);
        Assert.That(nonceChange, Is.EqualTo(nonceChangeDecoded));

        CodeChange codeChange = new()
        {
            BlockAccessIndex = 10,
            NewCode = [0, 50]
        };
        byte[] codeChangeBytes = Rlp.Encode(codeChange, RlpBehaviors.None).Bytes;
        CodeChange codeChangeDecoded = Rlp.Decode<CodeChange>(codeChangeBytes, RlpBehaviors.None);
        Assert.That(codeChange, Is.EqualTo(codeChangeDecoded));

        SortedDictionary<byte[], SlotChanges> storageChangesDict = new()
        {
            { slotChanges.Slot, slotChanges }
        };

        AccountChanges accountChanges = new()
        {
            Address = TestItem.AddressA,
            StorageChanges = storageChangesDict,
            StorageReads = [storageRead, storageRead],
            BalanceChanges = [balanceChange, balanceChange],
            NonceChanges = [nonceChange, nonceChange],
            CodeChanges = [codeChange]
        };
        byte[] accountChangesBytes = Rlp.Encode(accountChanges, RlpBehaviors.None).Bytes;
        AccountChanges accountChangesDecoded = Rlp.Decode<AccountChanges>(accountChangesBytes, RlpBehaviors.None);
        Assert.That(accountChanges, Is.EqualTo(accountChangesDecoded));

        SortedDictionary<Address, AccountChanges> accountChangesDict = new()
        {
            { accountChanges.Address, accountChanges }
        };

        BlockAccessList blockAccessList = new()
        {
            AccountChanges = accountChangesDict
        };
        byte[] blockAccessListBytes = Rlp.Encode(blockAccessList, RlpBehaviors.None).Bytes;
        BlockAccessList blockAccessListDecoded = Rlp.Decode<BlockAccessList>(blockAccessListBytes, RlpBehaviors.None);
        Assert.That(blockAccessList, Is.EqualTo(blockAccessListDecoded));
    }

    [Test]
    public async Task System_contracts_and_withdrawals()
    {
        using BasicTestBlockchain testBlockchain = await BasicTestBlockchain.Create(BuildContainer());

        IWorldState worldState = testBlockchain.WorldStateManager.GlobalWorldState;
        using IDisposable _ = worldState.BeginScope(IWorldState.PreGenesis);
        InitWorldState(worldState);

        ulong gasPrice = 2;
        long gasLimit = 100000;
        Transaction tx = Build.A.Transaction
            .WithTo(TestItem.AddressB)
            .WithSenderAddress(TestItem.AddressA)
            .WithValue(0)
            .WithGasPrice(gasPrice)
            .WithGasLimit(gasLimit)
            .TestObject;

        BlockHeader header = Build.A.BlockHeader
            .WithBaseFee(1)
            .WithNumber(1)
            .WithGasUsed(21000)
            .WithReceiptsRoot(new("0x056b23fbba480696b65fe5a59b8f2148a1299103c4f57df839233af2cf4ca2d2"))
            .WithStateRoot(new("0x7c14eebf21367805cab32e286e87f18191ce9286ff344b665fd7d278e2ee2b87"))
            .WithBlobGasUsed(0)
            .WithBeneficiary(TestItem.AddressC)
            .WithParentBeaconBlockRoot(Hash256.Zero)
            .WithRequestsHash(new("0xe3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"))
            .TestObject;

        Block block = Build.A.Block
            .WithTransactions(tx)
            .WithBaseFeePerGas(1)
            .WithHeader(header).TestObject;

        (Block processedBlock, TxReceipt[] _) = testBlockchain.BlockProcessor.ProcessOne(block, ProcessingOptions.None, NullBlockTracer.Instance, _spec, CancellationToken.None);

        BlockAccessList blockAccessList = Rlp.Decode<BlockAccessList>(processedBlock.BlockAccessList);
        SortedDictionary<Address, AccountChanges> accountChanges = blockAccessList.AccountChanges;
        Assert.That(accountChanges, Has.Count.EqualTo(5));

        List<BalanceChange> senderBalanceChanges = accountChanges[TestItem.AddressA].BalanceChanges;
        List<NonceChange> senderNonceChanges = accountChanges[TestItem.AddressA].NonceChanges;
        List<BalanceChange> toBalanceChanges = accountChanges[TestItem.AddressB].BalanceChanges;
        List<BalanceChange> beneficiaryBalanceChanges = accountChanges[TestItem.AddressC].BalanceChanges;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(senderBalanceChanges, Has.Count.EqualTo(1));
            Assert.That(senderBalanceChanges[0].PostBalance, Is.EqualTo(_accountBalance - gasPrice * GasCostOf.Transaction));

            Assert.That(senderNonceChanges, Has.Count.EqualTo(1));
            Assert.That(senderNonceChanges[0].NewNonce, Is.EqualTo(1));

            // zero balance change should not be recorded
            Assert.That(toBalanceChanges, Is.Empty);

            Assert.That(beneficiaryBalanceChanges, Has.Count.EqualTo(1));
            Assert.That(beneficiaryBalanceChanges[0].PostBalance, Is.EqualTo(new UInt256(GasCostOf.Transaction)));
        }
    }

    private static Action<ContainerBuilder> BuildContainer()
        => containerBuilder => containerBuilder.AddSingleton(_specProvider);

    private static void InitWorldState(IWorldState worldState)
    {
        worldState.CreateAccount(TestItem.AddressA, _accountBalance);
        worldState.CreateAccount(Eip4788Constants.BeaconRootsAddress, 1);
        worldState.CreateAccount(Eip7002Constants.WithdrawalRequestPredeployAddress, 0, Eip7002TestConstants.Nonce);
        worldState.InsertCode(Eip7002Constants.WithdrawalRequestPredeployAddress, Eip7002TestConstants.CodeHash, Eip7002TestConstants.Code, _specProvider.GenesisSpec);
        worldState.CreateAccount(Eip7251Constants.ConsolidationRequestPredeployAddress, 0, Eip7251TestConstants.Nonce);
        worldState.InsertCode(Eip7251Constants.ConsolidationRequestPredeployAddress, Eip7251TestConstants.CodeHash, Eip7251TestConstants.Code, _specProvider.GenesisSpec);

        worldState.Commit(_specProvider.GenesisSpec);
        worldState.CommitTree(0);
        worldState.RecalculateStateRoot();
        // Hash256 stateRoot = worldState.StateRoot;
    }
}
