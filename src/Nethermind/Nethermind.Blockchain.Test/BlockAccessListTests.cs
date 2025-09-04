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

        // add code change

        BlockHeader header = Build.A.BlockHeader
            .WithBaseFee(1)
            .WithNumber(1)
            .WithGasUsed(21000)
            .WithReceiptsRoot(new("0x056b23fbba480696b65fe5a59b8f2148a1299103c4f57df839233af2cf4ca2d2"))
            .WithStateRoot(new("0x869b0dea3e9d18f71753c2b64142901e11b6be272ddbb8975f32851528d30c36"))
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
            .WithTransactions(tx)
            .WithBaseFeePerGas(1)
            .WithWithdrawals([withdrawal])
            .WithHeader(header).TestObject;

        (Block processedBlock, TxReceipt[] _) = testBlockchain.BlockProcessor.ProcessOne(block, ProcessingOptions.None, NullBlockTracer.Instance, _spec, CancellationToken.None);

        BlockAccessList blockAccessList = Rlp.Decode<BlockAccessList>(processedBlock.BlockAccessList);
        SortedDictionary<Address, AccountChanges> accountChanges = blockAccessList.AccountChanges;
        Assert.That(accountChanges, Has.Count.EqualTo(8));

        AccountChanges addressAChanges = accountChanges[TestItem.AddressA];
        AccountChanges addressBChanges = accountChanges[TestItem.AddressB];
        AccountChanges addressCChanges = accountChanges[TestItem.AddressC];
        AccountChanges addressDChanges = accountChanges[TestItem.AddressD];
        AccountChanges eip2935Changes = accountChanges[Eip2935Constants.BlockHashHistoryAddress];
        // string k1 = Convert.ToHexString(eip2935Changes.StorageChanges.First().Key);
        // string v1 = Convert.ToHexString(eip2935Changes.StorageChanges.First().Value.Changes.First().NewValue.Unwrap());
        AccountChanges eip4788Changes = accountChanges[Eip4788Constants.BeaconRootsAddress];
        // string k2 = Convert.ToHexString(eip4788Changes.StorageChanges.First().Key);
        // string v2 = Convert.ToHexString(eip4788Changes.StorageChanges.First().Value.Changes.First().NewValue.Unwrap());
        // string k3 = Convert.ToHexString(eip4788Changes.StorageChanges.ElementAt(1).Key);
        // string v3 = Convert.ToHexString(eip4788Changes.StorageChanges.ElementAt(1).Value.Changes.First().NewValue.Unwrap());
        AccountChanges eip7002Changes = accountChanges[Eip7002Constants.WithdrawalRequestPredeployAddress];
        // string k4 = Convert.ToHexString(eip7002Changes.StorageChanges.First().Key);
        // string v4 = Convert.ToHexString(eip7002Changes.StorageChanges.First().Value.Changes.First().NewValue.Unwrap());
        // string k5 = Convert.ToHexString(eip7002Changes.StorageChanges.ElementAt(1).Key);
        // string v5 = Convert.ToHexString(eip7002Changes.StorageChanges.ElementAt(1).Value.Changes.First().NewValue.Unwrap());
        // string k6 = Convert.ToHexString(eip7002Changes.StorageChanges.ElementAt(2).Key);
        // string v6 = Convert.ToHexString(eip7002Changes.StorageChanges.ElementAt(2).Value.Changes.First().NewValue.Unwrap());
        // string k7 = Convert.ToHexString(eip7002Changes.StorageChanges.ElementAt(3).Key);
        // string v7 = Convert.ToHexString(eip7002Changes.StorageChanges.ElementAt(3).Value.Changes.First().NewValue.Unwrap());
        AccountChanges eip7251Changes = accountChanges[Eip7251Constants.ConsolidationRequestPredeployAddress];
        // string k8 = Convert.ToHexString(eip7251Changes.StorageChanges.First().Key);
        // string v8 = Convert.ToHexString(eip7251Changes.StorageChanges.First().Value.Changes.First().NewValue.Unwrap());
        // string k9 = Convert.ToHexString(eip7251Changes.StorageChanges.ElementAt(1).Key);
        // string v9 = Convert.ToHexString(eip7251Changes.StorageChanges.ElementAt(1).Value.Changes.First().NewValue.Unwrap());
        // string ka = Convert.ToHexString(eip7251Changes.StorageChanges.ElementAt(2).Key);
        // string va = Convert.ToHexString(eip7251Changes.StorageChanges.ElementAt(2).Value.Changes.First().NewValue.Unwrap());
        // string kb = Convert.ToHexString(eip7251Changes.StorageChanges.ElementAt(3).Key);
        // string vb = Convert.ToHexString(eip7251Changes.StorageChanges.ElementAt(3).Value.Changes.First().NewValue.Unwrap());

        // byte[] slot0 = Bytes.FromHexString("0x290DECD9548B62A8D60345A988386FC84BA6BC95484008F6362F93160EF3E563");
        byte[] slot0 = ToStorageSlot(0);
        // StorageChange parentHashStorageChange = new(0, Bytes32.Wrap(Bytes.FromHexString("0xFF483E972A04A9A62BB4B7D04AE403C615604E4090521ECC5BB7AF67F71BE09C")));
        StorageChange parentHashStorageChange = new(0, Bytes32.Wrap(parentHash.BytesToArray()));

        byte[] eip4788Slot2 = ValueKeccak.Compute(new BigInteger((timestamp % 8191) + 8191).ToBytes32(true)).ToByteArray();
        byte[] slot2Old = Bytes.FromHexString("0x0E59911BBD9B80FD816896F0425C7A25DC9EB9092F5AC6264B432F6697F877C8");
        StorageChange calldataStorageChange = new(0, Bytes32.Zero);

        byte[] eip4788Slot1 = ValueKeccak.Compute(new BigInteger(timestamp % 8191).ToBytes32(true)).ToByteArray();
        byte[] slot3Old = Bytes.FromHexString("0x2CFFC05BC4230E308FCB837385A814EED1B4C90FB58BA2A0B8407649B9629B28");
        // hash(timestamp)
        StorageChange timestampStorageChange = new(0, Bytes32.Wrap(Bytes.FromHexString("0x00000000000000000000000000000000000000000000000000000000000F4240")));

        // hash(0)
        // byte[] slot4 = ToStorageSlot(0);
        // byte[] slot0Old = Bytes.FromHexString("0x290DECD9548B62A8D60345A988386FC84BA6BC95484008F6362F93160EF3E563");
        StorageChange zeroStorageChangeEnd = new(2, Bytes32.Zero);
        // StorageChange value4 = new(2, Bytes32.Zero);

        // hash(2)
        byte[] slot2 = ToStorageSlot(2);
        // byte[] slot2Old = Bytes.FromHexString("0x405787FA12A823E0F2B7631CC41B3BA8828B3321CA811111FA75CD3AA3BB5ACE");
        // StorageChange value5 = new(2, Bytes32.Zero);

        // hash(1)
        byte[] slot1 = ToStorageSlot(1);
        // byte[] slot1Old = Bytes.FromHexString("0xB10E2D527612073B26EECDFD717E6A320CF44B4AFAC2B0732D9FCBE2B7FA0CF6");
        // StorageChange value6 = new(2, Bytes32.Zero);
        // StorageChange value6 = new(0, Bytes32.Wrap(Bytes.FromHexString("00000000000000000000000000000000000000000000000000000000000F4240")));

        // hash(3)
        byte[] slot3 = ToStorageSlot(3);
        // byte[] slot3Old = Bytes.FromHexString("0xC2575A0E9E593C00F959F8C92F12DB2869C3395A3B0502D05E2516446F71F85B");
        // StorageChange value7 = new(2, Bytes32.Zero);

        // hash(0)
        // byte[] slot8 = ToStorageSlot(0);
        // byte[] slot0Old = Bytes.FromHexString("0x290DECD9548B62A8D60345A988386FC84BA6BC95484008F6362F93160EF3E563");
        // StorageChange value8 = new(2, Bytes32.Zero);

        // hash(2)
        // byte[] slot2 = ToStorageSlot(2);
        // byte[] slot2Old = Bytes.FromHexString("0x405787FA12A823E0F2B7631CC41B3BA8828B3321CA811111FA75CD3AA3BB5ACE");
        // StorageChange value9 = new(2, Bytes32.Zero);

        // hash(1)
        // byte[] slota = ToStorageSlot(1);
        // byte[] slotaOld = Bytes.FromHexString("0xB10E2D527612073B26EECDFD717E6A320CF44B4AFAC2B0732D9FCBE2B7FA0CF6");
        // StorageChange valuea = new(2, Bytes32.Zero);
        // StorageChange valuea = new(2, Bytes32.Wrap(Bytes.FromHexString("00000000000000000000000000000000000000000000000000000000000F4240")));

        // hash(3)
        // byte[] slotb = ToStorageSlot(3);
        // byte[] slotbOld = Bytes.FromHexString("0xC2575A0E9E593C00F959F8C92F12DB2869C3395A3B0502D05E2516446F71F85B");
        // StorageChange valueb = new(2, Bytes32.Zero);

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

            Assert.That(eip2935Changes, Is.EqualTo(new AccountChanges()
            {
                Address = Eip2935Constants.BlockHashHistoryAddress,
                StorageChanges = new(Bytes.Comparer) { { slot0, new SlotChanges(slot0, [parentHashStorageChange]) } },
                StorageReads = [],
                BalanceChanges = [],
                NonceChanges = [],
                CodeChanges = []
            }));

            Assert.That(eip4788Changes, Is.EqualTo(new AccountChanges()
            {
                Address = Eip4788Constants.BeaconRootsAddress,
                StorageChanges = new(Bytes.Comparer) { { eip4788Slot1, new SlotChanges(eip4788Slot1, [timestampStorageChange]) }, { eip4788Slot2, new SlotChanges(eip4788Slot2, [calldataStorageChange]) } },
                StorageReads = [new(Bytes32.Wrap(Bytes.FromHexString("0x0e59911bbd9b80fd816896f0425c7a25dc9eb9092f5ac6264b432f6697f877c8")))],
                BalanceChanges = [],
                NonceChanges = [],
                CodeChanges = []
            }));

            Assert.That(eip7002Changes, Is.EqualTo(new AccountChanges()
            {
                Address = Eip7002Constants.WithdrawalRequestPredeployAddress,
                StorageChanges = new(Bytes.Comparer) { { slot0, new SlotChanges(slot0, [zeroStorageChangeEnd]) }, { slot2, new SlotChanges(slot2, [zeroStorageChangeEnd]) }, { slot1, new SlotChanges(slot1, [zeroStorageChangeEnd]) }, { slot3, new SlotChanges(slot3, [zeroStorageChangeEnd]) } },
                StorageReads = [
                    new(Bytes32.Wrap(Bytes.FromHexString("0xb10e2d527612073b26eecdfd717e6a320cf44b4afac2b0732d9fcbe2b7fa0cf6"))),
                    new(Bytes32.Wrap(Bytes.FromHexString("0x290decd9548b62a8d60345a988386fc84ba6bc95484008f6362f93160ef3e563"))),
                    new(Bytes32.Wrap(Bytes.FromHexString("0x405787fa12a823e0f2b7631cc41b3ba8828b3321ca811111fa75cd3aa3bb5ace"))),
                    new(Bytes32.Wrap(Bytes.FromHexString("0xc2575a0e9e593c00f959f8c92f12db2869c3395a3b0502d05e2516446f71f85b"))),
                ],
                BalanceChanges = [],
                NonceChanges = [],
                CodeChanges = []
            }));

            Assert.That(eip7251Changes, Is.EqualTo(new AccountChanges()
            {
                Address = Eip7251Constants.ConsolidationRequestPredeployAddress,
                StorageChanges = new(Bytes.Comparer) { { slot0, new SlotChanges(slot0, [zeroStorageChangeEnd]) }, { slot2, new SlotChanges(slot2, [zeroStorageChangeEnd]) }, { slot1, new SlotChanges(slot1, [zeroStorageChangeEnd]) }, { slot3, new SlotChanges(slot3, [zeroStorageChangeEnd]) } },
                StorageReads = [
                    new(Bytes32.Wrap(Bytes.FromHexString("0xb10e2d527612073b26eecdfd717e6a320cf44b4afac2b0732d9fcbe2b7fa0cf6"))),
                    new(Bytes32.Wrap(Bytes.FromHexString("0x290decd9548b62a8d60345a988386fc84ba6bc95484008f6362f93160ef3e563"))),
                    new(Bytes32.Wrap(Bytes.FromHexString("0x405787fa12a823e0f2b7631cc41b3ba8828b3321ca811111fa75cd3aa3bb5ace"))),
                    new(Bytes32.Wrap(Bytes.FromHexString("0xc2575a0e9e593c00f959f8c92f12db2869c3395a3b0502d05e2516446f71f85b"))),
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

        worldState.CreateAccount(Eip2935Constants.BlockHashHistoryAddress, 0, 1);
        byte[] eip2935Code = Bytes.FromHexString("0x3373fffffffffffffffffffffffffffffffffffffffe14604657602036036042575f35600143038111604257611fff81430311604257611fff9006545f5260205ff35b5f5ffd5b5f35611fff60014303065500");
        worldState.InsertCode(Eip2935Constants.BlockHashHistoryAddress, ValueKeccak.Compute(eip2935Code), eip2935Code, _specProvider.GenesisSpec);

        worldState.CreateAccount(Eip4788Constants.BeaconRootsAddress, 0, 1);
        byte[] eip4788Code = Bytes.FromHexString("0x3373fffffffffffffffffffffffffffffffffffffffe14604d57602036146024575f5ffd5b5f35801560495762001fff810690815414603c575f5ffd5b62001fff01545f5260205ff35b5f5ffd5b62001fff42064281555f359062001fff015500");
        worldState.InsertCode(Eip4788Constants.BeaconRootsAddress, ValueKeccak.Compute(eip4788Code), eip4788Code, _specProvider.GenesisSpec);

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
}
