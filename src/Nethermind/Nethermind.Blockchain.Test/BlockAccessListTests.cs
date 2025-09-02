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
    public async Task Can_construct_BAL()
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
            .WithStateRoot(new("0x791d07603a3449bd9d70180d3f00af1fb493033171c7c2b056bf07779c45cfe8"))
            .WithBlobGasUsed(0)
            .WithBeneficiary(TestItem.AddressC)
            .WithParentBeaconBlockRoot(Hash256.Zero)
            .WithRequestsHash(new("0xe3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"))
            .TestObject;

        Withdrawal withdrawal = new()
        {
            Index = 0,
            ValidatorIndex = 0,
            Address = TestItem.AddressD,
            AmountInGwei = 1
        };

        Block block = Build.A.Block
            .WithTransactions(tx)
            .WithBaseFeePerGas(1)
            .WithWithdrawals([withdrawal])
            .WithHeader(header).TestObject;

        (Block processedBlock, TxReceipt[] _) = testBlockchain.BlockProcessor.ProcessOne(block, ProcessingOptions.None, NullBlockTracer.Instance, _spec, CancellationToken.None);

        BlockAccessList blockAccessList = Rlp.Decode<BlockAccessList>(processedBlock.BlockAccessList);
        SortedDictionary<Address, AccountChanges> accountChanges = blockAccessList.AccountChanges;
        Assert.That(accountChanges, Has.Count.EqualTo(6));

        AccountChanges addressAChanges = accountChanges[TestItem.AddressA];
        AccountChanges addressBChanges = accountChanges[TestItem.AddressB];
        AccountChanges addressCChanges = accountChanges[TestItem.AddressC];
        AccountChanges addressDChanges = accountChanges[TestItem.AddressD];
        AccountChanges eip4788Changes = accountChanges[Eip4788Constants.BeaconRootsAddress];
        AccountChanges eip2935Changes = accountChanges[Eip2935Constants.BlockHashHistoryAddress];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(addressAChanges, Is.EqualTo(new AccountChanges()
            {
                Address = TestItem.AddressA,
                StorageChanges = [],
                StorageReads = [],
                BalanceChanges = [new(1, _accountBalance - gasPrice * GasCostOf.Transaction)],
                NonceChanges = [new(1, 1)],
                CodeChanges = []
            }));

            Assert.That(addressBChanges, Is.EqualTo(new AccountChanges()
            {
                Address = TestItem.AddressB,
                StorageChanges = [],
                StorageReads = [],
                BalanceChanges = [],
                NonceChanges = [],
                CodeChanges = []
            }));

            Assert.That(addressCChanges, Is.EqualTo(new AccountChanges()
            {
                Address = TestItem.AddressC,
                StorageChanges = [],
                StorageReads = [],
                BalanceChanges = [new(1, new UInt256(GasCostOf.Transaction))],
                NonceChanges = [new(1, 0)],
                CodeChanges = []
            }));

            Assert.That(addressDChanges, Is.EqualTo(new AccountChanges()
            {
                Address = TestItem.AddressD,
                StorageChanges = [],
                StorageReads = [],
                BalanceChanges = [new(2, 1.GWei())],
                NonceChanges = [],
                CodeChanges = []
            }));

            Assert.That(eip4788Changes, Is.EqualTo(new AccountChanges()
            {
                Address = Eip4788Constants.BeaconRootsAddress,
                StorageChanges = [],
                StorageReads = [],
                BalanceChanges = [],
                NonceChanges = [],
                CodeChanges = []
            }));

            Assert.That(eip2935Changes, Is.EqualTo(new AccountChanges()
            {
                Address = Eip2935Constants.BlockHashHistoryAddress,
                StorageChanges = [],
                StorageReads = [],
                BalanceChanges = [],
                NonceChanges = [],
                CodeChanges = []
            }));
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
