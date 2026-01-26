// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
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
using Nethermind.Serialization.Rlp.Eip7928;
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

    // todo: move to RLP tests?
    [TestCaseSource(nameof(BlockAccessListTestSource))]
    public void Can_decode_then_encode(string rlp, BlockAccessList expected)
    {
        BlockAccessList bal = Rlp.Decode<BlockAccessList>(Bytes.FromHexString(rlp).AsRlpStream());

        // Console.WriteLine(bal);
        // Console.WriteLine(expected);
        Assert.That(bal, Is.EqualTo(expected));

        string encoded = "0x" + Bytes.ToHexString(Rlp.Encode(bal).Bytes);
        Console.WriteLine(encoded);
        Console.WriteLine(rlp);
        Assert.That(encoded, Is.EqualTo(rlp));
    }

    [Test]
    public void Can_decode_then_encode_balance_change()
    {
        const string rlp = "0xc801861319718811c8";
        Rlp.ValueDecoderContext ctx = new(Bytes.FromHexString(rlp));
        BalanceChange balanceChange = BalanceChangeDecoder.Instance.Decode(ref ctx, RlpBehaviors.None);
        BalanceChange expected = new(1, 0x1319718811c8);
        Assert.That(balanceChange, Is.EqualTo(expected));

        string encoded = "0x" + Bytes.ToHexString(Rlp.Encode(balanceChange).Bytes);
        Console.WriteLine(encoded);
        Console.WriteLine(rlp);
        Assert.That(encoded, Is.EqualTo(rlp));
    }

    [Test]
    public void Can_decode_then_encode_nonce_change()
    {
        const string rlp = "0xc20101";
        Rlp.ValueDecoderContext ctx = new(Bytes.FromHexString(rlp));
        NonceChange nonceChange = NonceChangeDecoder.Instance.Decode(ref ctx, RlpBehaviors.None);
        NonceChange expected = new(1, 1);
        Assert.That(nonceChange, Is.EqualTo(expected));

        string encoded = "0x" + Bytes.ToHexString(Rlp.Encode(nonceChange).Bytes);
        Console.WriteLine(encoded);
        Console.WriteLine(rlp);
        Assert.That(encoded, Is.EqualTo(rlp));
    }

    [Test]
    public void Can_decode_then_encode_slot_change()
    {
        // Note: UInt256 constructor from bytes needs isBigEndian: true to match RLP encoding
        StorageChange parentHashStorageChange = new(0, new UInt256(Bytes.FromHexString("0xc382836f81d7e4055a0e280268371e17cc69a531efe2abee082e9b922d6050fd"), isBigEndian: true));
        SlotChanges expected = new(0, [parentHashStorageChange]);

        // Generate expected RLP from the object (uses variable-length encoding per EIP-7928)
        string expectedRlp = "0x" + Bytes.ToHexString(Rlp.Encode(expected).Bytes);

        Rlp.ValueDecoderContext ctx = new(Bytes.FromHexString(expectedRlp));
        SlotChanges slotChange = SlotChangesDecoder.Instance.Decode(ref ctx, RlpBehaviors.None);
        Assert.That(slotChange, Is.EqualTo(expected));

        string encoded = "0x" + Bytes.ToHexString(Rlp.Encode(slotChange).Bytes);
        Console.WriteLine(encoded);
        Console.WriteLine(expectedRlp);
        Assert.That(encoded, Is.EqualTo(expectedRlp));
    }

    [Test]
    public void Can_decode_then_encode_storage_change()
    {
        // Create expected StorageChange with a large UInt256 value
        // Note: UInt256 constructor from bytes uses little-endian, but we want the value 0xc382836f...
        // which when RLP encoded gives the same hex string as the value bytes
        StorageChange expected = new(0, new UInt256(Bytes.FromHexString("0xc382836f81d7e4055a0e280268371e17cc69a531efe2abee082e9b922d6050fd"), isBigEndian: true));

        // Generate expected RLP from the object (uses variable-length encoding per EIP-7928)
        string expectedRlp = "0x" + Bytes.ToHexString(Rlp.Encode(expected).Bytes);

        Rlp.ValueDecoderContext ctx = new(Bytes.FromHexString(expectedRlp));
        StorageChange storageChange = StorageChangeDecoder.Instance.Decode(ref ctx, RlpBehaviors.None);
        Assert.That(storageChange, Is.EqualTo(expected));

        string encoded = "0x" + Bytes.ToHexString(Rlp.Encode(storageChange).Bytes);
        Console.WriteLine(encoded);
        Console.WriteLine(expectedRlp);
        Assert.That(encoded, Is.EqualTo(expectedRlp));
    }

    [Test]
    public void Can_decode_then_encode_code_change()
    {
        const string rlp = "0xc20100";

        Rlp.ValueDecoderContext ctx = new(Bytes.FromHexString(rlp));
        CodeChange codeChange = CodeChangeDecoder.Instance.Decode(ref ctx, RlpBehaviors.None);
        CodeChange expected = new(1, [0x0]);
        Assert.That(codeChange, Is.EqualTo(expected));

        string encoded = "0x" + Bytes.ToHexString(Rlp.Encode(codeChange).Bytes);
        Console.WriteLine(encoded);
        Console.WriteLine(rlp);
        Assert.That(encoded, Is.EqualTo(rlp));
    }

    [TestCaseSource(nameof(AccountChangesTestSource))]
    public void Can_decode_then_encode_account_change(string rlp, AccountChanges expected)
    {
        Rlp.ValueDecoderContext ctx = new(Bytes.FromHexString(rlp));
        AccountChanges accountChange = AccountChangesDecoder.Instance.Decode(ref ctx, RlpBehaviors.None);

        Assert.That(accountChange, Is.EqualTo(expected));

        string encoded = "0x" + Bytes.ToHexString(Rlp.Encode(accountChange).Bytes);
        Console.WriteLine(encoded);
        Console.WriteLine(rlp);
        Assert.That(encoded, Is.EqualTo(rlp));
    }

    [Test]
    public void Can_encode_then_decode()
    {
        StorageChange storageChange = new()
        {
            BlockAccessIndex = 10,
            NewValue = 0xcad
        };
        byte[] storageChangeBytes = Rlp.Encode(storageChange, RlpBehaviors.None).Bytes;
        StorageChange storageChangeDecoded = Rlp.Decode<StorageChange>(storageChangeBytes, RlpBehaviors.None);
        Assert.That(storageChange, Is.EqualTo(storageChangeDecoded));

        SlotChanges slotChanges = new(0xbad, [storageChange, storageChange]);
        byte[] slotChangesBytes = Rlp.Encode(slotChanges, RlpBehaviors.None).Bytes;
        SlotChanges slotChangesDecoded = Rlp.Decode<SlotChanges>(slotChangesBytes, RlpBehaviors.None);
        Assert.That(slotChanges, Is.EqualTo(slotChangesDecoded));

        StorageRead storageRead = new(0xbababa);
        StorageRead storageRead2 = new(0xcacaca);
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

        SortedDictionary<UInt256, SlotChanges> storageChangesDict = new()
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

        AccountChanges accountChanges = new(
            TestItem.AddressA,
            storageChangesDict,
            [storageRead, storageRead2],
            balanceChangesList,
            nonceChangesList,
            codeChangesList
        );
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

        // Get the main world state which should be a ParallelWorldState after DI fix
        IWorldState mainWorldState = testBlockchain.MainWorldState;
        ParallelWorldState? tracedWorldState = mainWorldState as ParallelWorldState;
        Assert.That(tracedWorldState, Is.Not.Null, "Main world state should be ParallelWorldState");

        // Begin scope and initialize state
        using IDisposable _ = mainWorldState.BeginScope(IWorldState.PreGenesis);
        InitWorldState(mainWorldState);

        tracedWorldState!.BlockAccessList = new();

        const long gasUsed = 167340;
        const long gasUsedBeforeFinal = 92100;
        const ulong gasPrice = 2;
        const long gasLimit = 100000;
        const ulong timestamp = 1000000;
        Hash256 parentHash = new("0xff483e972a04a9a62bb4b7d04ae403c615604e4090521ecc5bb7af67f71be09c");
        // Hash256 parentHash = new("0x2971654f1af575a158b8541be71bea738a64d0c715c190e9c99ae5207c108d7d");

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
            .WithBlockAccessListHash(new("0xa19f3798cdc08ff0bdee830bb5daf6954ecbd8723c810285fef3240d06d2bf18"))
            .WithTimestamp(timestamp)
            .WithParentHash(parentHash)
            // .WithTotalDifficulty(1000000000L)
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
        // Block processedBlock = testBlockchain.BlockchainProcessor.Process(block, ProcessingOptions.None, NullBlockTracer.Instance)!;
        // Block[] res = testBlockchain.BranchProcessor.Process(header, [block], ProcessingOptions.None, NullBlockTracer.Instance, CancellationToken.None);
        // Blockchain.AddBlockResult res = testBlockchain.BlockTree.SuggestBlock(block);
        // testBlockchain.BlockTree.UpdateMainChain([block], true);
        // Block processedBlock = res[0];
        // Block processedBlock = Build.A.Block.TestObject;

        // GeneratedBlockAccessList is set by the block processor during execution
        BlockAccessList blockAccessList = processedBlock.GeneratedBlockAccessList!.Value;
        Assert.That(blockAccessList.AccountChanges.Count, Is.EqualTo(10));

        Address newContractAddress = ContractAddress.From(TestItem.AddressA, 1);
        Address newContractAddress2 = ContractAddress.From(TestItem.AddressA, 2);

        AccountChanges addressAChanges = blockAccessList.GetAccountChanges(TestItem.AddressA)!;
        AccountChanges addressBChanges = blockAccessList.GetAccountChanges(TestItem.AddressB)!;
        AccountChanges addressCChanges = blockAccessList.GetAccountChanges(TestItem.AddressC)!;
        AccountChanges addressDChanges = blockAccessList.GetAccountChanges(TestItem.AddressD)!;
        AccountChanges newContractChanges = blockAccessList.GetAccountChanges(newContractAddress)!;
        AccountChanges newContractChanges2 = blockAccessList.GetAccountChanges(newContractAddress2)!;
        AccountChanges eip2935Changes = blockAccessList.GetAccountChanges(Eip2935Constants.BlockHashHistoryAddress)!;
        AccountChanges eip4788Changes = blockAccessList.GetAccountChanges(Eip4788Constants.BeaconRootsAddress)!;
        AccountChanges eip7002Changes = blockAccessList.GetAccountChanges(Eip7002Constants.WithdrawalRequestPredeployAddress)!;
        AccountChanges eip7251Changes = blockAccessList.GetAccountChanges(Eip7251Constants.ConsolidationRequestPredeployAddress)!;

        UInt256 slot0 = 0;
        UInt256 slot1 = 1;
        UInt256 slot2 = 2;
        UInt256 slot3 = 3;
        UInt256 eip4788Slot1 = timestamp % Eip4788Constants.RingBufferSize;
        UInt256 eip4788Slot2 = (timestamp % Eip4788Constants.RingBufferSize) + Eip4788Constants.RingBufferSize;
        // UInt256 from bytes needs isBigEndian: true to match EVM storage encoding
        StorageChange parentHashStorageChange = new(0, new UInt256(parentHash.BytesToArray(), isBigEndian: true));
        StorageChange calldataStorageChange = new(0, 0);
        StorageChange timestampStorageChange = new(0, 0xF4240);
        StorageChange zeroStorageChangeEnd = new(3, 0);

        UInt256 addressABalance = _accountBalance - gasPrice * GasCostOf.Transaction;
        UInt256 addressABalance2 = _accountBalance - gasPrice * gasUsedBeforeFinal;
        UInt256 addressABalance3 = _accountBalance - gasPrice * gasUsed;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(addressAChanges, Is.EqualTo(new AccountChanges(
                TestItem.AddressA,
                [],
                [],
                new SortedList<ushort, BalanceChange> { { 1, new(1, addressABalance) }, { 2, new(2, addressABalance2) }, { 3, new(3, addressABalance3) } },
                new SortedList<ushort, NonceChange> { { 1, new(1, 1) }, { 2, new(2, 2) }, { 3, new(3, 3) } },
                []
            )));

            Assert.That(addressBChanges, Is.EqualTo(new AccountChanges(
                TestItem.AddressB,
                [],
                [],
                [],
                [],
                []
            )));

            Assert.That(addressCChanges, Is.EqualTo(new AccountChanges(
                TestItem.AddressC,
                [],
                [],
                new SortedList<ushort, BalanceChange> { { 1, new(1, new UInt256(GasCostOf.Transaction)) }, { 2, new(2, new UInt256(gasUsedBeforeFinal)) }, { 3, new(3, new UInt256(gasUsed)) } },
                [],
                []
            )));

            Assert.That(addressDChanges, Is.EqualTo(new AccountChanges(
                TestItem.AddressD,
                [],
                [],
                new SortedList<ushort, BalanceChange> { { 4, new(4, 1.GWei()) } },
                [],
                []
            )));

            Assert.That(newContractChanges, Is.EqualTo(new AccountChanges(
                newContractAddress,
                [],
                [],
                [],
                new SortedList<ushort, NonceChange> { { 2, new(2, 1) } },
                new SortedList<ushort, CodeChange> { { 2, new(2, Eip2935TestConstants.Code) } }
            )));

            Assert.That(newContractChanges2, Is.EqualTo(new AccountChanges(
                newContractAddress2,
                [],
                [new(1)],
                [],
                [],
                []
            )));

            Assert.That(eip2935Changes, Is.EqualTo(new AccountChanges(
                Eip2935Constants.BlockHashHistoryAddress,
                new SortedDictionary<UInt256, SlotChanges>() { { 0, new SlotChanges(0, [parentHashStorageChange]) } },
                [],
                [],
                [],
                []
            )));

            // eip4788 stores timestamp at slot1 and beacon root (0) at slot2
            // beacon root 0→0 is not a change, so only slot1 has a storage change
            // slot1 is not a separate read since it's already a change, only slot2 is read
            Assert.That(eip4788Changes, Is.EqualTo(new AccountChanges(
                Eip4788Constants.BeaconRootsAddress,
                new SortedDictionary<UInt256, SlotChanges>() {
                    { eip4788Slot1, new SlotChanges(eip4788Slot1, [timestampStorageChange]) }
                },
                [new(eip4788Slot2)],
                [],
                [],
                []
            )));

            // storage reads make no changes
            Assert.That(eip7002Changes, Is.EqualTo(new AccountChanges(
                Eip7002Constants.WithdrawalRequestPredeployAddress,
                [],
                [new(0), new(1), new(2), new(3)],
                [],
                [],
                []
            )));

            // storage reads make no changes
            Assert.That(eip7251Changes, Is.EqualTo(new AccountChanges(
                Eip7251Constants.ConsolidationRequestPredeployAddress,
                [],
                [new(0), new(1), new(2), new(3)],
                [],
                [],
                []
            )));
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

    private static IEnumerable<TestCaseData> AccountChangesTestSource
    {
        get
        {
            AccountChanges storageReadsExpected = new(
                Eip7002Constants.WithdrawalRequestPredeployAddress,
                [],
                [new(0), new(1), new(2), new(3)],
                [],
                [],
                []
            );
            // Generate RLP from object (uses variable-length encoding per EIP-7928)
            string storageReadsRlp = "0x" + Bytes.ToHexString(Rlp.Encode(storageReadsExpected).Bytes);
            yield return new TestCaseData(storageReadsRlp, storageReadsExpected)
            { TestName = "storage_reads" };

            AccountChanges storageChangesExpected = new(
                Eip2935Constants.BlockHashHistoryAddress,
                new SortedDictionary<UInt256, SlotChanges>() { { 0, new SlotChanges(0, [new(0, new UInt256(Bytes.FromHexString("0xc382836f81d7e4055a0e280268371e17cc69a531efe2abee082e9b922d6050fd"), isBigEndian: true))]) } },
                [],
                [],
                [],
                []
            );
            // Generate RLP from object (uses variable-length encoding per EIP-7928)
            string storageChangesRlp = "0x" + Bytes.ToHexString(Rlp.Encode(storageChangesExpected).Bytes);
            yield return new TestCaseData(storageChangesRlp, storageChangesExpected)
            { TestName = "storage_changes" };
        }
    }

    private static IEnumerable<TestCaseData> BlockAccessListTestSource
    {
        get
        {
            UInt256 eip4788Slot1 = 0xc;
            // Note: UInt256 constructor from bytes needs isBigEndian: true to match RLP encoding
            StorageChange parentHashStorageChange = new(0, new UInt256(Bytes.FromHexString("0xc382836f81d7e4055a0e280268371e17cc69a531efe2abee082e9b922d6050fd"), isBigEndian: true));
            // Note: value 0x0c (12) encoded as variable-length UInt256, not 32 bytes
            StorageChange timestampStorageChange = new(0, 0xc);
            SortedDictionary<Address, AccountChanges> expectedAccountChanges = new()
            {
                {Eip7002Constants.WithdrawalRequestPredeployAddress, new(
                    Eip7002Constants.WithdrawalRequestPredeployAddress,
                    [],
                    [new(0), new(1), new(2), new(3)],
                    [],
                    [],
                    []
                )},
                {Eip7251Constants.ConsolidationRequestPredeployAddress, new(
                    Eip7251Constants.ConsolidationRequestPredeployAddress,
                    [],
                    [new(0), new(1), new(2), new(3)],
                    [],
                    [],
                    []
                )},
                {Eip2935Constants.BlockHashHistoryAddress, new(
                    Eip2935Constants.BlockHashHistoryAddress,
                    new SortedDictionary<UInt256, SlotChanges>() { { 0, new SlotChanges(0, [parentHashStorageChange]) } },
                    [],
                    [],
                    [],
                    []
                )},
                {Eip4788Constants.BeaconRootsAddress, new(
                    Eip4788Constants.BeaconRootsAddress,
                    new SortedDictionary<UInt256, SlotChanges>() { { eip4788Slot1, new SlotChanges(eip4788Slot1, [timestampStorageChange]) } },
                    [new(0x200b)],
                    [],
                    [],
                    []
                )},
                {new("0x2adc25665018aa1fe0e6bc666dac8fc2697ff9ba"), new(
                    new("0x2adc25665018aa1fe0e6bc666dac8fc2697ff9ba"),
                    [],
                    [],
                    new SortedList<ushort, BalanceChange> { { 1, new(1, 0x1319718811c8) } },
                    [],
                    []
                )},
                {new("0xaccc7d92b051544a255b8a899071040739bada75"), new(
                    new("0xaccc7d92b051544a255b8a899071040739bada75"),
                    [],
                    [],
                    new SortedList<ushort, BalanceChange> { { 1, new(1, new UInt256(Bytes.FromHexString("0x3635c99aac6d15af9c"), isBigEndian: true)) } },
                    new SortedList<ushort, NonceChange> { { 1, new(1, 1) } },
                    []
                )},
                {new("0xd9c0e57d447779673b236c7423aeab84e931f3ba"), new(
                    new("0xd9c0e57d447779673b236c7423aeab84e931f3ba"),
                    [],
                    [],
                    new SortedList<ushort, BalanceChange> { { 1, new(1, 0x64) } },
                    [],
                    []
                )},
            };
            BlockAccessList expected = new(expectedAccountChanges);
            // Generate RLP from object (uses variable-length encoding per EIP-7928)
            string balanceChangesRlp = "0x" + Bytes.ToHexString(Rlp.Encode(expected).Bytes);
            yield return new TestCaseData(balanceChangesRlp, expected)
            { TestName = "balance_changes" };
        }
    }

}
