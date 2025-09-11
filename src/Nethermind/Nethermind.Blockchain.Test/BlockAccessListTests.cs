// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
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
using Nethermind.State;
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
        StorageRead storageRead2 = new(new Bytes32([.. Enumerable.Repeat<byte>(60, 32)]));
        byte[] storageReadBytes = Rlp.Encode(storageRead, RlpBehaviors.None).Bytes;
        StorageRead storageReadDecoded = Rlp.Decode<StorageRead>(storageReadBytes, RlpBehaviors.None);
        Assert.That(storageRead, Is.EqualTo(storageReadDecoded));

        BalanceChange balanceChange = new()
        {
            BlockAccessIndex = 10,
            PostBalance = 0
        };
        BalanceChange balanceChange2 = new()
        {
            BlockAccessIndex = 11,
            PostBalance = 1
        };
        byte[] balanceChangeBytes = Rlp.Encode(balanceChange, RlpBehaviors.None).Bytes;
        BalanceChange balanceChangeDecoded = Rlp.Decode<BalanceChange>(balanceChangeBytes, RlpBehaviors.None);
        Assert.That(balanceChange, Is.EqualTo(balanceChangeDecoded));

        NonceChange nonceChange = new()
        {
            BlockAccessIndex = 10,
            NewNonce = 0
        };
        NonceChange nonceChange2 = new()
        {
            BlockAccessIndex = 11,
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

        SortedList<ushort, BalanceChange> balanceChangesList = new()
        {
            { balanceChange.BlockAccessIndex, balanceChange },
            { balanceChange2.BlockAccessIndex, balanceChange2 }
        };

        SortedList<ushort, NonceChange> nonceChangesList = new()
        {
            { nonceChange.BlockAccessIndex, nonceChange },
            { nonceChange2.BlockAccessIndex, nonceChange2 }
        };

        SortedList<ushort, CodeChange> codeChangesList = new()
        {
            { codeChange.BlockAccessIndex, codeChange },
        };

        AccountChanges accountChanges = new()
        {
            Address = TestItem.AddressA,
            StorageChanges = storageChangesDict,
            StorageReads = [storageRead, storageRead2],
            BalanceChanges = balanceChangesList,
            NonceChanges = nonceChangesList,
            CodeChanges = codeChangesList
        };
        byte[] accountChangesBytes = Rlp.Encode(accountChanges, RlpBehaviors.None).Bytes;
        AccountChanges accountChangesDecoded = Rlp.Decode<AccountChanges>(accountChangesBytes, RlpBehaviors.None);
        Assert.That(accountChanges, Is.EqualTo(accountChangesDecoded));

        SortedDictionary<Address, AccountChanges> accountChangesDict = new()
        {
            { accountChanges.Address, accountChanges }
        };

        BlockAccessList blockAccessList = new(accountChangesDict);
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

        (worldState as TracedAccessWorldState)!.BlockAccessList = new();

        const long gasUsed = 167340;
        const long gasUsedBeforeFinal = 92100;
        const ulong gasPrice = 2;
        const long gasLimit = 100000;
        const ulong timestamp = 1000000;
        Hash256 parentHash = new("0xff483e972a04a9a62bb4b7d04ae403c615604e4090521ecc5bb7af67f71be09c");

        Transaction tx = Build.A.Transaction
            .WithTo(TestItem.AddressB)
            .WithSenderAddress(TestItem.AddressA)
            .WithValue(0)
            .WithGasPrice(gasPrice)
            .WithGasLimit(gasLimit)
            .TestObject;

        Transaction tx2 = Build.A.Transaction
            .WithTo(null)
            .WithSenderAddress(TestItem.AddressA)
            .WithValue(0)
            .WithNonce(1)
            .WithGasPrice(gasPrice)
            .WithGasLimit(gasLimit)
            .WithCode(Eip2935TestConstants.InitCode)
            .TestObject;

        /*
        Store followed by revert should undo storage change
        PUSH1 1
        PUSH1 1
        SSTORE
        PUSH0
        PUSH0
        REVERT
        */
        byte[] code = Bytes.FromHexString("0x60016001555f5ffd");
        Transaction tx3 = Build.A.Transaction
            .WithTo(null)
            .WithSenderAddress(TestItem.AddressA)
            .WithValue(0)
            .WithNonce(2)
            .WithGasPrice(gasPrice)
            .WithGasLimit(gasLimit)
            .WithCode(code)
            .TestObject;

        BlockHeader header = Build.A.BlockHeader
            .WithBaseFee(1)
            .WithNumber(1)
            .WithGasUsed(gasUsed)
            .WithReceiptsRoot(new("0x3d4548dff4e45f6e7838b223bf9476cd5ba4fd05366e8cb4e6c9b65763209569"))
            .WithStateRoot(new("0x9399acd9f2603778c11646f05f7827509b5319815da74b5721a07defb6285c8d"))
            .WithBlobGasUsed(0)
            .WithBeneficiary(TestItem.AddressC)
            .WithParentBeaconBlockRoot(Hash256.Zero)
            .WithRequestsHash(new("0xe3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"))
            .WithTimestamp(timestamp)
            .WithParentHash(parentHash)
            .TestObject;

        Withdrawal withdrawal = new()
        {
            Index = 0,
            ValidatorIndex = 0,
            Address = TestItem.AddressD,
            AmountInGwei = 1
        };

        Block block = Build.A.Block
            .WithTransactions([tx, tx2, tx3])
            .WithBaseFeePerGas(1)
            .WithWithdrawals([withdrawal])
            .WithHeader(header).TestObject;

        (Block processedBlock, TxReceipt[] _) = testBlockchain.BlockProcessor.ProcessOne(block, ProcessingOptions.None, NullBlockTracer.Instance, _spec, CancellationToken.None);

        BlockAccessList blockAccessList = Rlp.Decode<BlockAccessList>(processedBlock.BlockAccessList);
        Assert.That(blockAccessList.GetAccountChanges().Count, Is.EqualTo(10));

        Address newContractAddress = ContractAddress.From(TestItem.AddressA, 1);
        Address newContractAddress2 = ContractAddress.From(TestItem.AddressA, 2);

        AccountChanges addressAChanges = blockAccessList.GetAccountChanges(TestItem.AddressA)!.Value;
        AccountChanges addressBChanges = blockAccessList.GetAccountChanges(TestItem.AddressB)!.Value;
        AccountChanges addressCChanges = blockAccessList.GetAccountChanges(TestItem.AddressC)!.Value;
        AccountChanges addressDChanges = blockAccessList.GetAccountChanges(TestItem.AddressD)!.Value;
        AccountChanges newContractChanges = blockAccessList.GetAccountChanges(newContractAddress)!.Value;
        AccountChanges newContractChanges2 = blockAccessList.GetAccountChanges(newContractAddress2)!.Value;
        AccountChanges eip2935Changes = blockAccessList.GetAccountChanges(Eip2935Constants.BlockHashHistoryAddress)!.Value;
        AccountChanges eip4788Changes = blockAccessList.GetAccountChanges(Eip4788Constants.BeaconRootsAddress)!.Value;
        AccountChanges eip7002Changes = blockAccessList.GetAccountChanges(Eip7002Constants.WithdrawalRequestPredeployAddress)!.Value;
        AccountChanges eip7251Changes = blockAccessList.GetAccountChanges(Eip7251Constants.ConsolidationRequestPredeployAddress)!.Value;

        byte[] slot0 = ToStorageSlot(0);
        byte[] slot1 = ToStorageSlot(1);
        byte[] slot2 = ToStorageSlot(2);
        byte[] slot3 = ToStorageSlot(3);
        byte[] eip4788Slot1 = ToStorageSlot(timestamp % Eip4788Constants.RingBufferSize);
        byte[] eip4788Slot2 = ToStorageSlot((timestamp % Eip4788Constants.RingBufferSize) + Eip4788Constants.RingBufferSize);
        StorageChange parentHashStorageChange = new(0, Bytes32.Wrap(parentHash.BytesToArray()));
        StorageChange calldataStorageChange = new(0, Bytes32.Zero);
        StorageChange timestampStorageChange = new(0, Bytes32.Wrap(Bytes.FromHexString("0x00000000000000000000000000000000000000000000000000000000000F4240")));
        StorageChange zeroStorageChangeEnd = new(3, Bytes32.Zero);

        UInt256 addressABalance = _accountBalance - gasPrice * GasCostOf.Transaction;
        UInt256 addressABalance2 = _accountBalance - gasPrice * gasUsedBeforeFinal;
        UInt256 addressABalance3 = _accountBalance - gasPrice * gasUsed;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(addressAChanges, Is.EqualTo(new AccountChanges()
            {
                Address = TestItem.AddressA,
                StorageChanges = [],
                StorageReads = [],
                BalanceChanges = new SortedList<ushort, BalanceChange> { { 1, new(1, addressABalance) }, { 2, new(2, addressABalance2) }, { 3, new(3, addressABalance3) } },
                NonceChanges = new SortedList<ushort, NonceChange> { { 1, new(1, 1) }, { 2, new(2, 2) }, { 3, new(3, 3) } },
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
                BalanceChanges = new SortedList<ushort, BalanceChange> { { 1, new(1, new UInt256(GasCostOf.Transaction)) }, { 2, new(2, new UInt256(gasUsedBeforeFinal)) }, { 3, new(3, new UInt256(gasUsed)) } },
                NonceChanges = [],
                CodeChanges = []
            }));

            Assert.That(addressDChanges, Is.EqualTo(new AccountChanges()
            {
                Address = TestItem.AddressD,
                StorageChanges = [],
                StorageReads = [],
                BalanceChanges = new SortedList<ushort, BalanceChange> { { 4, new(4, 1.GWei()) } },
                NonceChanges = [],
                CodeChanges = []
            }));

            Assert.That(newContractChanges, Is.EqualTo(new AccountChanges()
            {
                Address = newContractAddress,
                StorageChanges = [],
                StorageReads = [],
                BalanceChanges = [],
                NonceChanges = new SortedList<ushort, NonceChange> { { 2, new(2, 1) } },
                CodeChanges = new SortedList<ushort, CodeChange> { { 2, new(2, Eip2935TestConstants.Code) } }
            }));

            Assert.That(newContractChanges2, Is.EqualTo(new AccountChanges()
            {
                Address = newContractAddress2,
                StorageChanges = [],
                StorageReads = [ToStorageRead(slot1)],
                BalanceChanges = [],
                NonceChanges = [],
                CodeChanges = []
            }));

            Assert.That(eip2935Changes, Is.EqualTo(new AccountChanges()
            {
                Address = Eip2935Constants.BlockHashHistoryAddress,
                StorageChanges = new(Bytes.Comparer) { { slot0, new SlotChanges(slot0, [parentHashStorageChange]) } },
                StorageReads = [],
                BalanceChanges = [],
                NonceChanges = [],
                CodeChanges = []
            }));

            // second storage read is not a change, so not recorded
            Assert.That(eip4788Changes, Is.EqualTo(new AccountChanges()
            {
                Address = Eip4788Constants.BeaconRootsAddress,
                StorageChanges = new(Bytes.Comparer) { { eip4788Slot1, new SlotChanges(eip4788Slot1, [timestampStorageChange]) } },
                StorageReads = [ToStorageRead(eip4788Slot1), ToStorageRead(eip4788Slot2)],
                BalanceChanges = [],
                NonceChanges = [],
                CodeChanges = []
            }));

            // storage reads make no changes
            Assert.That(eip7002Changes, Is.EqualTo(new AccountChanges()
            {
                Address = Eip7002Constants.WithdrawalRequestPredeployAddress,
                StorageChanges = [],
                StorageReads = [
                    ToStorageRead(slot0),
                    ToStorageRead(slot1),
                    ToStorageRead(slot2),
                    ToStorageRead(slot3),
                ],
                BalanceChanges = [],
                NonceChanges = [],
                CodeChanges = []
            }));

            // storage reads make no changes
            Assert.That(eip7251Changes, Is.EqualTo(new AccountChanges()
            {
                Address = Eip7251Constants.ConsolidationRequestPredeployAddress,
                StorageChanges = [],
                StorageReads = [
                    ToStorageRead(slot0),
                    ToStorageRead(slot1),
                    ToStorageRead(slot2),
                    ToStorageRead(slot3),
                ],
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

        worldState.CreateAccount(Eip2935Constants.BlockHashHistoryAddress, 0, Eip2935TestConstants.Nonce);
        worldState.InsertCode(Eip2935Constants.BlockHashHistoryAddress, Eip2935TestConstants.CodeHash, Eip2935TestConstants.Code, _specProvider.GenesisSpec);

        worldState.CreateAccount(Eip4788Constants.BeaconRootsAddress, 0, Eip4788TestConstants.Nonce);
        worldState.InsertCode(Eip4788Constants.BeaconRootsAddress, Eip4788TestConstants.CodeHash, Eip4788TestConstants.Code, _specProvider.GenesisSpec);

        worldState.CreateAccount(Eip7002Constants.WithdrawalRequestPredeployAddress, 0, Eip7002TestConstants.Nonce);
        worldState.InsertCode(Eip7002Constants.WithdrawalRequestPredeployAddress, Eip7002TestConstants.CodeHash, Eip7002TestConstants.Code, _specProvider.GenesisSpec);

        worldState.CreateAccount(Eip7251Constants.ConsolidationRequestPredeployAddress, 0, Eip7251TestConstants.Nonce);
        worldState.InsertCode(Eip7251Constants.ConsolidationRequestPredeployAddress, Eip7251TestConstants.CodeHash, Eip7251TestConstants.Code, _specProvider.GenesisSpec);

        worldState.Commit(_specProvider.GenesisSpec);
        worldState.CommitTree(0);
        worldState.RecalculateStateRoot();
        // Hash256 stateRoot = worldState.StateRoot;
    }

    private static byte[] ToStorageSlot(ulong x)
        => ValueKeccak.Compute(new BigInteger(x).ToBytes32(true)).ToByteArray();

    private static StorageRead ToStorageRead(byte[] x)
        => new(Bytes32.Wrap(x));
}
