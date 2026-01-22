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

        // Get the main world state which should be a TracedAccessWorldState after DI fix
        IWorldState mainWorldState = testBlockchain.MainWorldState;
        TracedAccessWorldState? tracedWorldState = mainWorldState as TracedAccessWorldState;
        Assert.That(tracedWorldState, Is.Not.Null, "Main world state should be TracedAccessWorldState");

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

    [Test]
    public void StorageWrite_ThenRevert_ShouldResultInStorageRead()
    {
        // Scenario: SSTORE then REVERT
        // Expected: The slot should be tracked as a storage READ (since the write was reverted)
        BlockAccessList bal = new();
        Address address = TestItem.AddressA;
        UInt256 slot = 1;
        UInt256 originalValue = 100;
        UInt256 newValue = 200;

        // Take snapshot before the write
        int snapshot = bal.TakeSnapshot();

        // Simulate SSTORE (write to storage)
        bal.AddStorageChange(address, slot, originalValue, newValue);

        // Verify we have a storage change
        AccountChanges? accountChanges = bal.GetAccountChanges(address);
        Assert.That(accountChanges, Is.Not.Null);
        Assert.That(accountChanges!.StorageChanges.Count(), Is.EqualTo(1));
        Assert.That(accountChanges.StorageReads.Count(), Is.EqualTo(0));

        // Revert to before the write
        bal.Restore(snapshot);

        // After revert, the slot should be tracked as a storage READ
        accountChanges = bal.GetAccountChanges(address);
        Assert.That(accountChanges, Is.Not.Null);
        Assert.That(accountChanges!.StorageChanges.Count(), Is.EqualTo(0), "Storage changes should be empty after revert");
        Assert.That(accountChanges.StorageReads.Count(), Is.EqualTo(1), "Should have one storage read after revert");
        Assert.That(accountChanges.StorageReads.First().Key, Is.EqualTo(slot), "Storage read should be for the reverted slot");
    }

    [Test]
    public void StorageRead_ThenStorageWrite_ThenRevert_ShouldResultInStorageRead()
    {
        // Scenario: SLOAD, SSTORE, REVERT
        // Expected: The slot should be tracked as a storage READ
        BlockAccessList bal = new();
        Address address = TestItem.AddressA;
        UInt256 slot = 1;
        UInt256 originalValue = 100;
        UInt256 newValue = 200;

        // Simulate SLOAD (read from storage)
        bal.AddStorageRead(address, slot);

        // Verify we have a storage read
        AccountChanges? accountChanges = bal.GetAccountChanges(address);
        Assert.That(accountChanges, Is.Not.Null);
        Assert.That(accountChanges!.StorageReads.Count(), Is.EqualTo(1));

        // Take snapshot before the write
        int snapshot = bal.TakeSnapshot();

        // Simulate SSTORE (write to storage) - this should remove the read and add a change
        bal.AddStorageChange(address, slot, originalValue, newValue);

        // Verify storage read was converted to storage change
        accountChanges = bal.GetAccountChanges(address);
        Assert.That(accountChanges!.StorageChanges.Count(), Is.EqualTo(1), "Should have storage change after SSTORE");
        Assert.That(accountChanges.StorageReads.Count(), Is.EqualTo(0), "Storage read should be removed after SSTORE");

        // Revert to before the write
        bal.Restore(snapshot);

        // After revert, we should have the storage read back
        accountChanges = bal.GetAccountChanges(address);
        Assert.That(accountChanges, Is.Not.Null);
        Assert.That(accountChanges!.StorageChanges.Count(), Is.EqualTo(0), "Storage changes should be empty after revert");
        Assert.That(accountChanges.StorageReads.Count(), Is.EqualTo(1), "Should have storage read after revert");
        Assert.That(accountChanges.StorageReads.First().Key, Is.EqualTo(slot));
    }

    [Test]
    public void TwoStorageWrites_RevertSecond_ShouldRestoreFirstChange()
    {
        // Scenario: SSTORE, SSTORE, REVERT second
        // Expected: First storage change should be restored
        BlockAccessList bal = new();
        Address address = TestItem.AddressA;
        UInt256 slot = 1;
        UInt256 originalValue = 100;
        UInt256 firstWriteValue = 200;
        UInt256 secondWriteValue = 300;

        // First SSTORE
        bal.AddStorageChange(address, slot, originalValue, firstWriteValue);

        // Verify first storage change
        AccountChanges? accountChanges = bal.GetAccountChanges(address);
        Assert.That(accountChanges!.StorageChanges.Count(), Is.EqualTo(1));
        SlotChanges slotChanges = accountChanges.StorageChanges.First();
        Assert.That(slotChanges.Changes.Count, Is.EqualTo(1));
        Assert.That(slotChanges.Changes.First().NewValue, Is.EqualTo(firstWriteValue));

        // Take snapshot before second write
        int snapshot = bal.TakeSnapshot();

        // Second SSTORE
        bal.AddStorageChange(address, slot, firstWriteValue, secondWriteValue);

        // Verify second storage change replaced first
        accountChanges = bal.GetAccountChanges(address);
        slotChanges = accountChanges!.StorageChanges.First();
        Assert.That(slotChanges.Changes.Count, Is.EqualTo(1));
        Assert.That(slotChanges.Changes.First().NewValue, Is.EqualTo(secondWriteValue));

        // Revert to before second write
        bal.Restore(snapshot);

        // First storage change should be restored
        accountChanges = bal.GetAccountChanges(address);
        Assert.That(accountChanges, Is.Not.Null);
        Assert.That(accountChanges!.StorageChanges.Count(), Is.EqualTo(1), "Should still have one storage change");
        Assert.That(accountChanges.StorageReads.Count(), Is.EqualTo(0), "Should have no storage reads");
        slotChanges = accountChanges.StorageChanges.First();
        Assert.That(slotChanges.Changes.Count, Is.EqualTo(1));
        Assert.That(slotChanges.Changes.First().NewValue, Is.EqualTo(firstWriteValue), "First write value should be restored");
    }

    [Test]
    public void StorageRead_TwoStorageWrites_RevertSecond_ShouldHaveFirstChangeNoRead()
    {
        // Scenario: SLOAD, SSTORE, SSTORE, REVERT second
        // Expected: First storage change should be restored, no storage read
        BlockAccessList bal = new();
        Address address = TestItem.AddressA;
        UInt256 slot = 1;
        UInt256 originalValue = 100;
        UInt256 firstWriteValue = 200;
        UInt256 secondWriteValue = 300;

        // SLOAD
        bal.AddStorageRead(address, slot);

        // First SSTORE (removes read, adds change)
        bal.AddStorageChange(address, slot, originalValue, firstWriteValue);

        // Verify: change exists, read removed
        AccountChanges? accountChanges = bal.GetAccountChanges(address);
        Assert.That(accountChanges!.StorageChanges.Count(), Is.EqualTo(1));
        Assert.That(accountChanges.StorageReads.Count(), Is.EqualTo(0));

        // Take snapshot before second write
        int snapshot = bal.TakeSnapshot();

        // Second SSTORE
        bal.AddStorageChange(address, slot, firstWriteValue, secondWriteValue);

        // Revert to before second write
        bal.Restore(snapshot);

        // First storage change should be restored, NO storage read
        accountChanges = bal.GetAccountChanges(address);
        Assert.That(accountChanges, Is.Not.Null);
        Assert.That(accountChanges!.StorageChanges.Count(), Is.EqualTo(1), "Should have first storage change");
        Assert.That(accountChanges.StorageReads.Count(), Is.EqualTo(0), "Should NOT have storage read - it was converted to change");
        SlotChanges slotChanges = accountChanges.StorageChanges.First();
        Assert.That(slotChanges.Changes.First().NewValue, Is.EqualTo(firstWriteValue));
    }

    [Test]
    public void MultipleSlots_PartialRevert_ShouldOnlyAffectRevertedSlot()
    {
        // Scenario: Write to slot 1, write to slot 2, revert slot 2 write
        // Expected: Slot 1 has change, slot 2 has read
        BlockAccessList bal = new();
        Address address = TestItem.AddressA;
        UInt256 slot1 = 1;
        UInt256 slot2 = 2;
        UInt256 originalValue = 100;
        UInt256 newValue = 200;

        // Write to slot 1
        bal.AddStorageChange(address, slot1, originalValue, newValue);

        // Take snapshot
        int snapshot = bal.TakeSnapshot();

        // Write to slot 2
        bal.AddStorageChange(address, slot2, originalValue, newValue);

        // Verify both changes exist
        AccountChanges? accountChanges = bal.GetAccountChanges(address);
        Assert.That(accountChanges!.StorageChanges.Count(), Is.EqualTo(2));

        // Revert slot 2 write
        bal.Restore(snapshot);

        // Slot 1 should have change, slot 2 should have read
        accountChanges = bal.GetAccountChanges(address);
        Assert.That(accountChanges!.StorageChanges.Count(), Is.EqualTo(1), "Should have one storage change (slot 1)");
        Assert.That(accountChanges.StorageReads.Count(), Is.EqualTo(1), "Should have one storage read (slot 2)");
        Assert.That(accountChanges.StorageChanges.First().Slot, Is.EqualTo(slot1));
        Assert.That(accountChanges.StorageReads.First().Key, Is.EqualTo(slot2));
    }

    [Test]
    public void NestedSnapshots_FullRevert_ShouldRestoreAllReads()
    {
        // Scenario: Snapshot, SSTORE slot1, Snapshot, SSTORE slot2, Revert all
        // Expected: Both slots should be reads
        BlockAccessList bal = new();
        Address address = TestItem.AddressA;
        UInt256 slot1 = 1;
        UInt256 slot2 = 2;
        UInt256 originalValue = 100;
        UInt256 newValue = 200;

        // Take initial snapshot
        int snapshot1 = bal.TakeSnapshot();

        // Write to slot 1
        bal.AddStorageChange(address, slot1, originalValue, newValue);

        // Take second snapshot
        int snapshot2 = bal.TakeSnapshot();

        // Write to slot 2
        bal.AddStorageChange(address, slot2, originalValue, newValue);

        // Verify both changes exist
        AccountChanges? accountChanges = bal.GetAccountChanges(address);
        Assert.That(accountChanges!.StorageChanges.Count(), Is.EqualTo(2));

        // Revert all the way to initial snapshot
        bal.Restore(snapshot1);

        // Both slots should be reads
        accountChanges = bal.GetAccountChanges(address);
        Assert.That(accountChanges!.StorageChanges.Count(), Is.EqualTo(0), "Should have no storage changes");
        Assert.That(accountChanges.StorageReads.Count(), Is.EqualTo(2), "Should have two storage reads");
    }

    [Test]
    public void WriteToSameValueAsOriginal_ShouldNotCreateChange()
    {
        // Scenario: SSTORE slot with same value as original
        // Expected: No storage change (optimization)
        BlockAccessList bal = new();
        Address address = TestItem.AddressA;
        UInt256 slot = 1;
        UInt256 originalValue = 100;

        // Write same value
        bal.AddStorageChange(address, slot, originalValue, originalValue);

        // Should not create a change
        AccountChanges? accountChanges = bal.GetAccountChanges(address);
        Assert.That(accountChanges, Is.Null.Or.Property("StorageChanges").Empty);
    }

    [Test]
    public void StorageRead_DoesNotCreateRevertibleChange()
    {
        // Scenario: SLOAD creates a read that is NOT reverted by Restore
        // This is expected behavior - reads track access, not state changes
        BlockAccessList bal = new();
        Address address = TestItem.AddressA;
        UInt256 slot = 1;

        // Take snapshot
        int snapshot = bal.TakeSnapshot();

        // SLOAD
        bal.AddStorageRead(address, slot);

        // Verify read exists
        AccountChanges? accountChanges = bal.GetAccountChanges(address);
        Assert.That(accountChanges!.StorageReads.Count(), Is.EqualTo(1));

        // Restore should NOT remove the read (reads are not revertible on their own)
        bal.Restore(snapshot);

        // Read should still exist
        accountChanges = bal.GetAccountChanges(address);
        Assert.That(accountChanges!.StorageReads.Count(), Is.EqualTo(1),
            "Storage reads are tracked for block access and should persist after revert");
    }

    [Test]
    public void StorageRead_ThenWrite_ThenRevert_ShouldHaveOneRead()
    {
        // Scenario: SLOAD, SLOAD (same slot), SSTORE, REVERT
        // Expected: One storage read (not duplicated)
        BlockAccessList bal = new();
        Address address = TestItem.AddressA;
        UInt256 slot = 1;
        UInt256 originalValue = 100;
        UInt256 newValue = 200;

        // First SLOAD
        bal.AddStorageRead(address, slot);

        // Second SLOAD (same slot - should not duplicate)
        bal.AddStorageRead(address, slot);

        // Verify only one read
        AccountChanges? accountChanges = bal.GetAccountChanges(address);
        Assert.That(accountChanges!.StorageReads.Count(), Is.EqualTo(1));

        // Take snapshot
        int snapshot = bal.TakeSnapshot();

        // SSTORE
        bal.AddStorageChange(address, slot, originalValue, newValue);

        // Revert
        bal.Restore(snapshot);

        // Should have exactly one read
        accountChanges = bal.GetAccountChanges(address);
        Assert.That(accountChanges!.StorageReads.Count(), Is.EqualTo(1), "Should have exactly one storage read");
        Assert.That(accountChanges.StorageChanges.Count(), Is.EqualTo(0), "Should have no storage changes");
    }

    [Test]
    public void BalanceChange_ThenRevert_ShouldRemoveChange()
    {
        // Test balance change revert behavior for completeness
        BlockAccessList bal = new();
        Address address = TestItem.AddressA;
        UInt256 originalBalance = 1000;
        UInt256 newBalance = 500;

        // Take snapshot
        int snapshot = bal.TakeSnapshot();

        // Add balance change
        bal.AddBalanceChange(address, originalBalance, newBalance);

        // Verify change exists
        AccountChanges? accountChanges = bal.GetAccountChanges(address);
        Assert.That(accountChanges!.BalanceChanges.Count(), Is.EqualTo(1));

        // Revert
        bal.Restore(snapshot);

        // Balance change should be removed
        accountChanges = bal.GetAccountChanges(address);
        Assert.That(accountChanges!.BalanceChanges.Count(), Is.EqualTo(0), "Balance change should be reverted");
    }

    [Test]
    public void ThreeStorageWrites_RevertToFirst_ShouldRestoreFirstChange()
    {
        // Scenario: SSTORE v1, SSTORE v2, SSTORE v3, REVERT to after first write
        // Expected: First storage change should be restored
        BlockAccessList bal = new();
        Address address = TestItem.AddressA;
        UInt256 slot = 1;
        UInt256 v0 = 100; // original
        UInt256 v1 = 200;
        UInt256 v2 = 300;
        UInt256 v3 = 400;

        // First SSTORE
        bal.AddStorageChange(address, slot, v0, v1);

        // Take snapshot after first write
        int snapshotAfterFirst = bal.TakeSnapshot();

        // Second SSTORE
        bal.AddStorageChange(address, slot, v1, v2);

        // Third SSTORE
        bal.AddStorageChange(address, slot, v2, v3);

        // Verify we have v3
        AccountChanges? accountChanges = bal.GetAccountChanges(address);
        SlotChanges slotChanges = accountChanges!.StorageChanges.First();
        Assert.That(slotChanges.Changes.First().NewValue, Is.EqualTo(v3));

        // Revert to after first write
        bal.Restore(snapshotAfterFirst);

        // Should have v1
        accountChanges = bal.GetAccountChanges(address);
        Assert.That(accountChanges!.StorageChanges.Count(), Is.EqualTo(1));
        slotChanges = accountChanges.StorageChanges.First();
        Assert.That(slotChanges.Changes.First().NewValue, Is.EqualTo(v1), "Should restore to first write value");
    }

    [Test]
    public void WriteRevertWriteRevert_ShouldEndWithRead()
    {
        // Scenario: SSTORE, REVERT, SSTORE, REVERT
        // Expected: Storage read (from the second revert)
        BlockAccessList bal = new();
        Address address = TestItem.AddressA;
        UInt256 slot = 1;
        UInt256 v0 = 100;
        UInt256 v1 = 200;
        UInt256 v2 = 300;

        // First write/revert cycle
        int snapshot1 = bal.TakeSnapshot();
        bal.AddStorageChange(address, slot, v0, v1);
        bal.Restore(snapshot1);

        // After first revert, should have a read
        AccountChanges? accountChanges = bal.GetAccountChanges(address);
        Assert.That(accountChanges!.StorageReads.Count(), Is.EqualTo(1), "Should have read after first revert");
        Assert.That(accountChanges.StorageChanges.Count(), Is.EqualTo(0));

        // Second write/revert cycle
        int snapshot2 = bal.TakeSnapshot();
        bal.AddStorageChange(address, slot, v0, v2); // Note: original value is still v0

        // After second write, should have change (read converted)
        accountChanges = bal.GetAccountChanges(address);
        Assert.That(accountChanges!.StorageChanges.Count(), Is.EqualTo(1));
        Assert.That(accountChanges.StorageReads.Count(), Is.EqualTo(0));

        bal.Restore(snapshot2);

        // After second revert, should have read again
        accountChanges = bal.GetAccountChanges(address);
        Assert.That(accountChanges!.StorageReads.Count(), Is.EqualTo(1), "Should have read after second revert");
        Assert.That(accountChanges.StorageChanges.Count(), Is.EqualTo(0), "Should have no changes after second revert");
    }

    [Test]
    public void DifferentAddresses_RevertOnlyAffectsTargetAddress()
    {
        // Scenario: Write to address A, write to address B, revert B's write
        // Expected: A has change, B has read
        BlockAccessList bal = new();
        Address addressA = TestItem.AddressA;
        Address addressB = TestItem.AddressB;
        UInt256 slot = 1;
        UInt256 v0 = 100;
        UInt256 v1 = 200;

        // Write to address A
        bal.AddStorageChange(addressA, slot, v0, v1);

        // Take snapshot
        int snapshot = bal.TakeSnapshot();

        // Write to address B
        bal.AddStorageChange(addressB, slot, v0, v1);

        // Revert
        bal.Restore(snapshot);

        // Address A should still have change
        AccountChanges? accountChangesA = bal.GetAccountChanges(addressA);
        Assert.That(accountChangesA!.StorageChanges.Count(), Is.EqualTo(1), "Address A should keep its change");

        // Address B should have read
        AccountChanges? accountChangesB = bal.GetAccountChanges(addressB);
        Assert.That(accountChangesB!.StorageReads.Count(), Is.EqualTo(1), "Address B should have read after revert");
        Assert.That(accountChangesB.StorageChanges.Count(), Is.EqualTo(0), "Address B should have no changes");
    }

    [Test]
    public void Line287Fix_TwoWrites_RevertSecond_ShouldNotHaveSpuriousRead()
    {
        // This test specifically targets the fix on line 287:
        // accountChanges.RemoveStorageRead(change.Slot.Value);
        //
        // The scenario: When reverting a storage change that had a previous storage change,
        // we should ensure no spurious storage read exists.
        BlockAccessList bal = new();
        Address address = TestItem.AddressA;
        UInt256 slot = 1;
        UInt256 v0 = 100;
        UInt256 v1 = 200;
        UInt256 v2 = 300;

        // First write: v0 -> v1
        bal.AddStorageChange(address, slot, v0, v1);

        // Verify: one change, no reads
        AccountChanges? accountChanges = bal.GetAccountChanges(address);
        Assert.That(accountChanges!.StorageChanges.Count(), Is.EqualTo(1));
        Assert.That(accountChanges.StorageReads.Count(), Is.EqualTo(0));

        // Take snapshot
        int snapshot = bal.TakeSnapshot();

        // Second write: v1 -> v2
        bal.AddStorageChange(address, slot, v1, v2);

        // Verify: still one change (updated), no reads
        accountChanges = bal.GetAccountChanges(address);
        Assert.That(accountChanges!.StorageChanges.Count(), Is.EqualTo(1));
        Assert.That(accountChanges.StorageReads.Count(), Is.EqualTo(0));
        Assert.That(accountChanges.StorageChanges.First().Changes.First().NewValue, Is.EqualTo(v2));

        // Revert second write - this triggers the code path with previousStorage != null
        bal.Restore(snapshot);

        // After revert: first change restored, NO spurious read
        accountChanges = bal.GetAccountChanges(address);
        Assert.That(accountChanges!.StorageChanges.Count(), Is.EqualTo(1), "First change should be restored");
        Assert.That(accountChanges.StorageReads.Count(), Is.EqualTo(0), "Should NOT have any storage reads - line 287 fix ensures this");
        Assert.That(accountChanges.StorageChanges.First().Changes.First().NewValue, Is.EqualTo(v1), "Should have v1 value");
    }

    [Test]
    public void Line287Fix_ReadWriteWrite_RevertSecond_ShouldHaveFirstChangeNoRead()
    {
        // Scenario: SLOAD, SSTORE, SSTORE, REVERT second
        // After first SSTORE: read removed, change added
        // After second SSTORE: change updated
        // After REVERT: first change restored, NO read (it was removed by first SSTORE)
        BlockAccessList bal = new();
        Address address = TestItem.AddressA;
        UInt256 slot = 1;
        UInt256 v0 = 100;
        UInt256 v1 = 200;
        UInt256 v2 = 300;

        // SLOAD: adds read
        bal.AddStorageRead(address, slot);
        AccountChanges? accountChanges = bal.GetAccountChanges(address);
        Assert.That(accountChanges!.StorageReads.Count(), Is.EqualTo(1), "Should have read after SLOAD");

        // First SSTORE: v0 -> v1 (removes read, adds change)
        bal.AddStorageChange(address, slot, v0, v1);
        accountChanges = bal.GetAccountChanges(address);
        Assert.That(accountChanges!.StorageReads.Count(), Is.EqualTo(0), "Read should be removed after SSTORE");
        Assert.That(accountChanges.StorageChanges.Count(), Is.EqualTo(1), "Should have change after SSTORE");

        // Take snapshot
        int snapshot = bal.TakeSnapshot();

        // Second SSTORE: v1 -> v2
        bal.AddStorageChange(address, slot, v1, v2);

        // Revert second SSTORE
        bal.Restore(snapshot);

        // Should have first change, NO read
        // The line 287 fix ensures any spurious read is removed, but in this case
        // there shouldn't be one anyway since the read was removed by first SSTORE
        accountChanges = bal.GetAccountChanges(address);
        Assert.That(accountChanges!.StorageChanges.Count(), Is.EqualTo(1), "First change should be restored");
        Assert.That(accountChanges.StorageReads.Count(), Is.EqualTo(0), "Should NOT have read - it was removed by first SSTORE");
        Assert.That(accountChanges.StorageChanges.First().Changes.First().NewValue, Is.EqualTo(v1));
    }

    [Test]
    public void Line287Fix_SimulateSubcallRevert_ShouldHandleCorrectly()
    {
        // Simulate a subcall scenario:
        // 1. Main call writes to slot
        // 2. Subcall writes to same slot
        // 3. Subcall reverts
        // Expected: Main call's change should be restored
        BlockAccessList bal = new();
        Address address = TestItem.AddressA;
        UInt256 slot = 1;
        UInt256 v0 = 100;
        UInt256 v1 = 200; // main call value
        UInt256 v2 = 300; // subcall value

        // Main call writes: v0 -> v1
        bal.AddStorageChange(address, slot, v0, v1);

        // Take snapshot (simulate subcall entry)
        int subcallSnapshot = bal.TakeSnapshot();

        // Subcall writes: v1 -> v2
        bal.AddStorageChange(address, slot, v1, v2);

        // Verify subcall value
        AccountChanges? accountChanges = bal.GetAccountChanges(address);
        Assert.That(accountChanges!.StorageChanges.First().Changes.First().NewValue, Is.EqualTo(v2));

        // Subcall reverts
        bal.Restore(subcallSnapshot);

        // Main call's value should be restored
        accountChanges = bal.GetAccountChanges(address);
        Assert.That(accountChanges!.StorageChanges.Count(), Is.EqualTo(1));
        Assert.That(accountChanges.StorageReads.Count(), Is.EqualTo(0));
        Assert.That(accountChanges.StorageChanges.First().Changes.First().NewValue, Is.EqualTo(v1));
    }

    [Test]
    public void Line287Fix_SubcallReadsThenWrites_MainCallHadChange_Revert_ShouldRestoreMainChange()
    {
        // Scenario:
        // 1. Main call writes slot (v0 -> v1)
        // 2. Subcall reads slot (SLOAD - should NOT add read because change exists)
        // 3. Subcall writes slot (v1 -> v2)
        // 4. Subcall reverts
        // Expected: Main call's change (v1) restored, no reads
        BlockAccessList bal = new();
        Address address = TestItem.AddressA;
        UInt256 slot = 1;
        UInt256 v0 = 100;
        UInt256 v1 = 200;
        UInt256 v2 = 300;

        // Main call writes
        bal.AddStorageChange(address, slot, v0, v1);

        // Take subcall snapshot
        int subcallSnapshot = bal.TakeSnapshot();

        // Subcall tries to read - but AddStorageRead checks if change exists first
        bal.AddStorageRead(address, slot);

        // Verify: read should NOT be added because change exists
        AccountChanges? accountChanges = bal.GetAccountChanges(address);
        Assert.That(accountChanges!.StorageReads.Count(), Is.EqualTo(0), "Read should NOT be added when change exists");

        // Subcall writes
        bal.AddStorageChange(address, slot, v1, v2);

        // Subcall reverts
        bal.Restore(subcallSnapshot);

        // Main call's change should be restored, no reads
        accountChanges = bal.GetAccountChanges(address);
        Assert.That(accountChanges!.StorageChanges.Count(), Is.EqualTo(1));
        Assert.That(accountChanges.StorageReads.Count(), Is.EqualTo(0));
        Assert.That(accountChanges.StorageChanges.First().Changes.First().NewValue, Is.EqualTo(v1));
    }

    [Test]
    public void Line287Fix_MultipleRevertLevels_ShouldHandleCorrectly()
    {
        // Deep nesting scenario:
        // Main: write v1
        // Sub1: write v2
        // Sub2: write v3
        // Revert Sub2 -> should have v2
        // Revert Sub1 -> should have v1
        BlockAccessList bal = new();
        Address address = TestItem.AddressA;
        UInt256 slot = 1;
        UInt256 v0 = 100;
        UInt256 v1 = 200;
        UInt256 v2 = 300;
        UInt256 v3 = 400;

        // Main writes
        bal.AddStorageChange(address, slot, v0, v1);
        int snapshot1 = bal.TakeSnapshot();

        // Sub1 writes
        bal.AddStorageChange(address, slot, v1, v2);
        int snapshot2 = bal.TakeSnapshot();

        // Sub2 writes
        bal.AddStorageChange(address, slot, v2, v3);

        // Verify v3
        AccountChanges? accountChanges = bal.GetAccountChanges(address);
        Assert.That(accountChanges!.StorageChanges.First().Changes.First().NewValue, Is.EqualTo(v3));

        // Revert Sub2
        bal.Restore(snapshot2);
        accountChanges = bal.GetAccountChanges(address);
        Assert.That(accountChanges!.StorageChanges.First().Changes.First().NewValue, Is.EqualTo(v2), "Should have v2 after Sub2 revert");
        Assert.That(accountChanges.StorageReads.Count(), Is.EqualTo(0), "No reads");

        // Revert Sub1
        bal.Restore(snapshot1);
        accountChanges = bal.GetAccountChanges(address);
        Assert.That(accountChanges!.StorageChanges.First().Changes.First().NewValue, Is.EqualTo(v1), "Should have v1 after Sub1 revert");
        Assert.That(accountChanges.StorageReads.Count(), Is.EqualTo(0), "No reads");
    }

    [Test]
    public void Line287Fix_WriteToZeroValue_ShouldBeTrackedCorrectly()
    {
        // Edge case: Writing zero value should still be tracked as a change
        BlockAccessList bal = new();
        Address address = TestItem.AddressA;
        UInt256 slot = 1;
        UInt256 v0 = 100;
        UInt256 v1 = 0; // Writing zero

        // Write zero
        bal.AddStorageChange(address, slot, v0, v1);

        // Should have a change (not a read)
        AccountChanges? accountChanges = bal.GetAccountChanges(address);
        Assert.That(accountChanges!.StorageChanges.Count(), Is.EqualTo(1), "Should track change to zero");
        Assert.That(accountChanges.StorageChanges.First().Changes.First().NewValue, Is.EqualTo(UInt256.Zero));

        // Take snapshot
        int snapshot = bal.TakeSnapshot();

        // Write non-zero
        bal.AddStorageChange(address, slot, v1, 500);

        // Revert
        bal.Restore(snapshot);

        // Should have change back to zero
        accountChanges = bal.GetAccountChanges(address);
        Assert.That(accountChanges!.StorageChanges.Count(), Is.EqualTo(1));
        Assert.That(accountChanges.StorageChanges.First().Changes.First().NewValue, Is.EqualTo(UInt256.Zero));
    }

    [Test]
    public void SelfDestruct_ConvertsStorageChangesToReads()
    {
        // Verify that SelfDestruct converts storage changes to reads
        BlockAccessList bal = new();
        Address address = TestItem.AddressA;
        UInt256 slot1 = 1;
        UInt256 slot2 = 2;
        UInt256 v0 = 100;
        UInt256 v1 = 200;

        // Write to two slots
        bal.AddStorageChange(address, slot1, v0, v1);
        bal.AddStorageChange(address, slot2, v0, v1);

        // Verify changes exist
        AccountChanges? accountChanges = bal.GetAccountChanges(address);
        Assert.That(accountChanges!.StorageChanges.Count(), Is.EqualTo(2), "Should have two storage changes");
        Assert.That(accountChanges.StorageReads.Count(), Is.EqualTo(0), "Should have no reads");

        // SelfDestruct (via DeleteAccount)
        bal.DeleteAccount(address, 1000); // balance = 1000

        // Changes should be converted to reads
        accountChanges = bal.GetAccountChanges(address);
        Assert.That(accountChanges!.StorageChanges.Count(), Is.EqualTo(0), "Storage changes should be cleared after selfdestruct");
        Assert.That(accountChanges.StorageReads.Count(), Is.EqualTo(2), "Should have two storage reads after selfdestruct");
        Assert.That(accountChanges.BalanceChanges.Count(), Is.EqualTo(1), "Should have balance change for selfdestruct");
    }

    [Test]
    public void SelfDestruct_ThenRevert_ShouldRestoreStorageChanges()
    {
        // After the fix: When selfdestruct is reverted, storage changes are properly restored.
        // This ensures the block access list accurately reflects the final state of storage.
        BlockAccessList bal = new();
        Address address = TestItem.AddressA;
        UInt256 slot = 1;
        UInt256 v0 = 100;
        UInt256 v1 = 200;
        UInt256 balance = 1000;

        // Write to slot
        bal.AddStorageChange(address, slot, v0, v1);

        // Verify change exists
        AccountChanges? accountChanges = bal.GetAccountChanges(address);
        Assert.That(accountChanges!.StorageChanges.Count(), Is.EqualTo(1), "Should have storage change before selfdestruct");
        Assert.That(accountChanges.StorageReads.Count(), Is.EqualTo(0));

        // Take snapshot
        int snapshot = bal.TakeSnapshot();

        // SelfDestruct (converts storage changes to reads)
        bal.DeleteAccount(address, balance);

        // Verify selfdestruct state
        accountChanges = bal.GetAccountChanges(address);
        Assert.That(accountChanges!.StorageChanges.Count(), Is.EqualTo(0), "Storage changes should be cleared after selfdestruct");
        Assert.That(accountChanges.StorageReads.Count(), Is.EqualTo(1), "Should have storage read after selfdestruct");

        // Revert
        bal.Restore(snapshot);

        // Storage changes should be restored
        accountChanges = bal.GetAccountChanges(address);
        Assert.That(accountChanges!.BalanceChanges.Count(), Is.EqualTo(0), "Balance change should be reverted");
        Assert.That(accountChanges.StorageChanges.Count(), Is.EqualTo(1), "Storage changes should be restored after revert");
        Assert.That(accountChanges.StorageReads.Count(), Is.EqualTo(0), "Storage reads from selfdestruct should be removed after revert");
        Assert.That(accountChanges.StorageChanges.First().Changes.First().NewValue, Is.EqualTo(v1), "Storage change value should be restored");
    }

    [Test]
    public void SelfDestruct_WithNoStorageChanges_JustAddsBalanceChange()
    {
        BlockAccessList bal = new();
        Address address = TestItem.AddressA;
        UInt256 balance = 1000;

        // SelfDestruct without any prior storage changes
        bal.DeleteAccount(address, balance);

        AccountChanges? accountChanges = bal.GetAccountChanges(address);
        Assert.That(accountChanges!.StorageChanges.Count(), Is.EqualTo(0));
        Assert.That(accountChanges.StorageReads.Count(), Is.EqualTo(0));
        Assert.That(accountChanges.BalanceChanges.Count(), Is.EqualTo(1));
        Assert.That(accountChanges.BalanceChanges.First().PostBalance, Is.EqualTo(UInt256.Zero));
    }

    [Test]
    public void SelfDestruct_ThenRevert_ShouldRestoreMultipleStorageChanges()
    {
        // Test with multiple storage slots
        BlockAccessList bal = new();
        Address address = TestItem.AddressA;
        UInt256 slot1 = 1;
        UInt256 slot2 = 2;
        UInt256 slot3 = 3;
        UInt256 v0 = 100;
        UInt256 v1 = 200;
        UInt256 v2 = 300;
        UInt256 v3 = 400;
        UInt256 balance = 1000;

        // Write to multiple slots
        bal.AddStorageChange(address, slot1, v0, v1);
        bal.AddStorageChange(address, slot2, v0, v2);
        bal.AddStorageChange(address, slot3, v0, v3);

        // Take snapshot
        int snapshot = bal.TakeSnapshot();

        // SelfDestruct
        bal.DeleteAccount(address, balance);

        // Verify all converted to reads
        AccountChanges? accountChanges = bal.GetAccountChanges(address);
        Assert.That(accountChanges!.StorageChanges.Count(), Is.EqualTo(0));
        Assert.That(accountChanges.StorageReads.Count(), Is.EqualTo(3));

        // Revert
        bal.Restore(snapshot);

        // All storage changes should be restored
        accountChanges = bal.GetAccountChanges(address);
        Assert.That(accountChanges!.StorageChanges.Count(), Is.EqualTo(3), "All storage changes should be restored");
        Assert.That(accountChanges.StorageReads.Count(), Is.EqualTo(0), "No storage reads after revert");

        // Verify each slot's value
        SlotChanges[] slots = [.. accountChanges.StorageChanges];
        Assert.That(slots.Any(s => s.Slot == slot1 && s.Changes.First().NewValue == v1), "Slot 1 should be restored");
        Assert.That(slots.Any(s => s.Slot == slot2 && s.Changes.First().NewValue == v2), "Slot 2 should be restored");
        Assert.That(slots.Any(s => s.Slot == slot3 && s.Changes.First().NewValue == v3), "Slot 3 should be restored");
    }

    [Test]
    public void SelfDestruct_ThenRevert_ShouldRestoreNonceAndCodeChanges()
    {
        // Test that nonce and code changes are also restored
        BlockAccessList bal = new();
        Address address = TestItem.AddressA;
        UInt256 balance = 1000;

        // Add nonce change
        bal.AddNonceChange(address, 5);

        // Add code change
        bal.AddCodeChange(address, [], [0x60, 0x00]);

        // Verify changes exist
        AccountChanges? accountChanges = bal.GetAccountChanges(address);
        Assert.That(accountChanges!.NonceChanges.Count(), Is.EqualTo(1));
        Assert.That(accountChanges.CodeChanges.Count(), Is.EqualTo(1));

        // Take snapshot
        int snapshot = bal.TakeSnapshot();

        // SelfDestruct (clears nonce and code changes)
        bal.DeleteAccount(address, balance);

        // Verify cleared
        accountChanges = bal.GetAccountChanges(address);
        Assert.That(accountChanges!.NonceChanges.Count(), Is.EqualTo(0), "Nonce changes should be cleared after selfdestruct");
        Assert.That(accountChanges.CodeChanges.Count(), Is.EqualTo(0), "Code changes should be cleared after selfdestruct");

        // Revert
        bal.Restore(snapshot);

        // Nonce and code changes should be restored
        accountChanges = bal.GetAccountChanges(address);
        Assert.That(accountChanges!.NonceChanges.Count(), Is.EqualTo(1), "Nonce changes should be restored");
        Assert.That(accountChanges.CodeChanges.Count(), Is.EqualTo(1), "Code changes should be restored");
        Assert.That(accountChanges.NonceChanges.First().NewNonce, Is.EqualTo(5ul));
    }

    [Test]
    public void SelfDestruct_InSubcall_ThenRevert_ShouldRestoreMainCallChanges()
    {
        // Simulate: Main call writes, subcall selfdestructs, subcall reverts
        BlockAccessList bal = new();
        Address address = TestItem.AddressA;
        UInt256 slot = 1;
        UInt256 v0 = 100;
        UInt256 v1 = 200;
        UInt256 balance = 1000;

        // Main call writes
        bal.AddStorageChange(address, slot, v0, v1);

        // Take snapshot (subcall entry)
        int subcallSnapshot = bal.TakeSnapshot();

        // Subcall selfdestructs
        bal.DeleteAccount(address, balance);

        // Verify selfdestruct state
        AccountChanges? accountChanges = bal.GetAccountChanges(address);
        Assert.That(accountChanges!.StorageChanges.Count(), Is.EqualTo(0));
        Assert.That(accountChanges.StorageReads.Count(), Is.EqualTo(1));

        // Subcall reverts
        bal.Restore(subcallSnapshot);

        // Main call's change should be restored
        accountChanges = bal.GetAccountChanges(address);
        Assert.That(accountChanges!.StorageChanges.Count(), Is.EqualTo(1), "Main call's storage change should be restored");
        Assert.That(accountChanges.StorageReads.Count(), Is.EqualTo(0));
        Assert.That(accountChanges.StorageChanges.First().Changes.First().NewValue, Is.EqualTo(v1));
    }

    [Test]
    public void SelfDestruct_WithExistingReads_ShouldNotAffectUnrelatedReads()
    {
        // Test that existing storage reads (from other slots) are not affected
        BlockAccessList bal = new();
        Address address = TestItem.AddressA;
        UInt256 slot1 = 1;
        UInt256 slot2 = 2;
        UInt256 v0 = 100;
        UInt256 v1 = 200;
        UInt256 balance = 1000;

        // Read slot 1 (via AddStorageRead - simulating SLOAD without SSTORE)
        bal.AddStorageRead(address, slot1);

        // Write slot 2
        bal.AddStorageChange(address, slot2, v0, v1);

        // Verify initial state
        AccountChanges? accountChanges = bal.GetAccountChanges(address);
        Assert.That(accountChanges!.StorageReads.Count(), Is.EqualTo(1), "Should have read for slot 1");
        Assert.That(accountChanges.StorageChanges.Count(), Is.EqualTo(1), "Should have change for slot 2");

        // Take snapshot
        int snapshot = bal.TakeSnapshot();

        // SelfDestruct
        bal.DeleteAccount(address, balance);

        // Verify: slot 1 read persists, slot 2 converted to read
        accountChanges = bal.GetAccountChanges(address);
        Assert.That(accountChanges!.StorageReads.Count(), Is.EqualTo(2), "Should have reads for both slots");
        Assert.That(accountChanges.StorageChanges.Count(), Is.EqualTo(0));

        // Revert
        bal.Restore(snapshot);

        // Slot 1 should still be a read, slot 2 should be restored as a change
        accountChanges = bal.GetAccountChanges(address);
        Assert.That(accountChanges!.StorageReads.Count(), Is.EqualTo(1), "Slot 1 should still be a read");
        Assert.That(accountChanges.StorageReads.First().Key, Is.EqualTo(slot1));
        Assert.That(accountChanges.StorageChanges.Count(), Is.EqualTo(1), "Slot 2 should be restored as change");
        Assert.That(accountChanges.StorageChanges.First().Slot, Is.EqualTo(slot2));
    }
}
