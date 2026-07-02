// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Test.Helpers;
using Nethermind.Xdc.Types;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Xdc.Test.ModuleTests;

internal class SubnetPenaltyTests
{
    private const int EpochLength = 90;
    private const int Gap = 45;
    private const long MergeSignRange = 5;
    private const ulong RangeReturnSigner = 15;
    private const int ValidatorCount = 20;


    [Test]
    public void TestNoPenalty()
    {
        MockedSubnetPenaltyContext ctx = CreateMockedContext(
            chainLength: EpochLength * 3 - Gap + 1,
            validatorSelector: (validators, i) => validators[i % ValidatorCount]
        );

        ulong target = EpochLength * 3 - Gap;
        Address[] penalties = ctx.Handler.HandlePenalties(
            target,
            ctx.Headers[(int)(target - 1)].Hash!,
            []);

        Assert.That(penalties, Is.Empty);
    }


    [TestCase(2, TestName = "CurrentEpoch")]
    [TestCase(1, TestName = "PreviousEpoch")]
    public void TestNoMinedBlocksInEpochPenalty(int absentEpoch)
    {
        // The last validator never mines (Beneficiary cycles 0..n-2) in the specified epoch.
        MockedSubnetPenaltyContext ctx = CreateMockedContext(
            chainLength: EpochLength * 3 - Gap + 1,
            validatorSelector: (validators, i) => i / EpochLength == absentEpoch ? validators[i % (ValidatorCount - 1)] : validators[i % ValidatorCount]
        );

        ulong target = EpochLength * 3 - Gap;
        Address[] penalties = ctx.Handler.HandlePenalties(
            target,
            ctx.Headers[(int)(target - 1)].Hash!,
            []);

        Assert.That(penalties.Length, Is.EqualTo(1));
        Assert.That(penalties[0], Is.EqualTo(ctx.ValidatorKeys.Last().Address));
    }

    [Test]
    public void TestKeepPreviousPenalties()
    {
        MockedSubnetPenaltyContext ctx = CreateMockedContext(
            chainLength: EpochLength * 3 - Gap + 1,
            validatorSelector: (validators, i) => validators[i % ValidatorCount]
        );

        ulong target = EpochLength * 3 - Gap;
        int targetHeaderIndex = (int)target - EpochLength + 1;

        Address[] injectedPenalties = [TestItem.AddressA, TestItem.AddressB];
        byte[] penaltyBytes = new byte[injectedPenalties.Length * Address.Size];
        for (int i = 0; i < injectedPenalties.Length; i++)
            injectedPenalties[i].Bytes.CopyTo(penaltyBytes.AsSpan(i * Address.Size));
        ctx.Headers[targetHeaderIndex].Penalties = penaltyBytes;

        Address[] penalties = ctx.Handler.HandlePenalties(
            target,
            ctx.Headers[(int)(target - 1)].Hash!,
            []);

        Assert.That(penalties, Is.EquivalentTo(injectedPenalties));
    }

    [Test]
    public void TestSigningTxRemovesPenalty()
    {
        // The last validator never mines (Beneficiary cycles 0..n-2) in current epoch.
        MockedSubnetPenaltyContext ctx = CreateMockedContext(
            chainLength: EpochLength * 3 - Gap + 1,
            validatorSelector: (validators, i) => i / EpochLength == 2 ? validators[i % (ValidatorCount - 1)] : validators[i % ValidatorCount]
        );

        ulong target = EpochLength * 3 - Gap;
        ulong signBlock = (target - 1) / MergeSignRange * MergeSignRange;
        BlockHeader header = ctx.Headers[(int)signBlock];
        Transaction tx = BuildSigningTx(ctx.Spec, header.Number, header.Hash!, ctx.ValidatorKeys.Last());
        ctx.HashToBlock[header.Hash!] = new Block(header, [tx], Array.Empty<BlockHeader>());

        Address[] penalties = ctx.Handler.HandlePenalties(
            target,
            ctx.Headers[(int)(target - 1)].Hash!,
            []);
        Assert.That(penalties, Is.Empty);
    }

    [Test]
    public void TestSigningTxBeforeRangeReturnSigner()
    {
        // The last validator never mines (Beneficiary cycles 0..n-2) in current epoch.
        MockedSubnetPenaltyContext ctx = CreateMockedContext(
            chainLength: EpochLength * 3 - Gap + 1,
            validatorSelector: (validators, i) => i / EpochLength == 2 ? validators[i % (ValidatorCount - 1)] : validators[i % ValidatorCount]
        );

        ulong target = EpochLength * 3 - Gap;
        ulong signBlock = (target - 1 - RangeReturnSigner) / MergeSignRange * MergeSignRange;
        BlockHeader header = ctx.Headers[(int)signBlock];
        Transaction tx = BuildSigningTx(ctx.Spec, header.Number, header.Hash!, ctx.ValidatorKeys.Last());
        ctx.HashToBlock[header.Hash!] = new Block(header, [tx], Array.Empty<BlockHeader>());

        Address[] penalties = ctx.Handler.HandlePenalties(
            target,
            ctx.Headers[(int)(target - 1)].Hash!,
            []);
        Assert.That(penalties.Length, Is.EqualTo(1));
        Assert.That(penalties[0], Is.EqualTo(ctx.ValidatorKeys.Last().Address));
    }


    [Test]
    public void TestSigningTxForNonMergeSignRangeBlock()
    {
        // The last validator never mines (Beneficiary cycles 0..n-2) in current epoch.
        MockedSubnetPenaltyContext ctx = CreateMockedContext(
            chainLength: EpochLength * 3 - Gap + 1,
            validatorSelector: (validators, i) => i / EpochLength == 2 ? validators[i % (ValidatorCount - 1)] : validators[i % ValidatorCount]
        );

        ulong target = EpochLength * 3 - Gap;
        ulong signBlock = (target - 1) / MergeSignRange * MergeSignRange - 1;
        BlockHeader header = ctx.Headers[(int)signBlock];
        Transaction tx = BuildSigningTx(ctx.Spec, header.Number, header.Hash!, ctx.ValidatorKeys.Last());
        ctx.HashToBlock[header.Hash!] = new Block(header, [tx], Array.Empty<BlockHeader>());

        Address[] penalties = ctx.Handler.HandlePenalties(
            target,
            ctx.Headers[(int)(target - 1)].Hash!,
            []);
        Assert.That(penalties.Length, Is.EqualTo(1));
        Assert.That(penalties[0], Is.EqualTo(ctx.ValidatorKeys.Last().Address));
    }

    [Test]
    public void TestSortOrder()
    {
        MockedSubnetPenaltyContext ctx = CreateMockedContext(
            chainLength: EpochLength * 3 - Gap + 1,
            validatorSelector: (validators, i) => validators[i % ValidatorCount]
        );

        ulong target = EpochLength * 3 - Gap;
        int targetHeaderIndex = (int)target - EpochLength + 1;

        Address eip55First = new("0xECf1aC276D2D3333483cF394d2F73BaB6915feCb");
        Address eip55Second = new("0xe3eE640071486df6A007021c34D52b5DE7be94e3");

        Assert.That(string.CompareOrdinal(eip55First.ToString(), eip55Second.ToString()), Is.GreaterThan(0));
        Assert.That(string.CompareOrdinal(eip55First.ToString(withEip55Checksum: true), eip55Second.ToString(withEip55Checksum: true)), Is.LessThan(0));

        Address[] injectedPenalties = [eip55Second, eip55First];
        byte[] penaltyBytes = new byte[injectedPenalties.Length * Address.Size];
        for (int i = 0; i < injectedPenalties.Length; i++)
            injectedPenalties[i].Bytes.CopyTo(penaltyBytes.AsSpan(i * Address.Size));
        ctx.Headers[targetHeaderIndex].Penalties = penaltyBytes;

        Address[] penalties = ctx.Handler.HandlePenalties(
            target,
            ctx.Headers[(int)(target - 1)].Hash!,
            []);

        Assert.That(penalties, Is.EqualTo(new[] { eip55First, eip55Second }));
    }

    // ─── Helpers ──────────────────────────────────────────────────────

    private static Transaction BuildSigningTx(
        IXdcReleaseSpec spec, ulong blockNumber, Hash256 blockHash, PrivateKey signer, ulong nonce = 0) =>
        Build.A.Transaction
            .WithChainId(0)
            .WithNonce(nonce)
            .WithGasLimit(200000)
            .WithXdcSigningData(blockNumber, blockHash)
            .ToBlockSignerContract(spec)
            .SignedAndResolved(signer)
            .TestObject;

    // ─── Mocked context factory ───────────────────────────────────────

    private static MockedSubnetPenaltyContext CreateMockedContext(
        int chainLength,
        Func<Address[], int, Address> validatorSelector)
    {
        PrivateKey[] validatorKeys = XdcTestHelper.GeneratePrivateKeys(ValidatorCount);
        Address[] validatorAddresses = new Address[validatorKeys.Length];
        for (int i = 0; i < validatorKeys.Length; i++)
            validatorAddresses[i] = validatorKeys[i].Address;

        IBlockTree blockTree = Substitute.For<IBlockTree>();

        XdcSubnetBlockHeader[] headers = new XdcSubnetBlockHeader[chainLength];
        Block[] blocks = new Block[chainLength];
        Dictionary<Hash256, XdcSubnetBlockHeader> hashToHeader = [];
        Dictionary<Hash256, Block> hashToBlock = [];

        for (int i = 0; i < chainLength; i++)
        {
            Hash256 parentHash = i == 0 ? Hash256.Zero : headers[i - 1].Hash!;
            XdcSubnetBlockHeaderBuilder builder = Build.A.XdcSubnetBlockHeader();
            builder.WithNumber(i);
            builder.WithParentHash(parentHash);
            builder.WithValidators(validatorAddresses);
            builder.WithExtraConsensusData(new ExtraFieldsV2((ulong)i, Build.A.QuorumCertificate().TestObject));
            headers[i] = builder.TestObject;

            headers[i].Beneficiary = validatorSelector(validatorAddresses, i);

            Hash256 hash = headers[i].Hash ?? headers[i].CalculateHash().ToHash256();
            hashToHeader[hash] = headers[i];
            blocks[i] = new Block(headers[i]);
            hashToBlock[hash] = blocks[i];
        }

        blockTree.FindHeader(Arg.Any<Hash256>(), Arg.Any<ulong?>())
            .Returns(ci => hashToHeader.TryGetValue(ci.ArgAt<Hash256>(0), out XdcSubnetBlockHeader? h) ? h : null);
        blockTree.FindBlock(Arg.Any<Hash256>(), Arg.Any<ulong?>())
            .Returns(ci => hashToBlock.TryGetValue(ci.ArgAt<Hash256>(0), out Block? block) ? block : null);
        blockTree.Head.Returns(blocks.Last());

        XdcReleaseSpec releaseSpec = new()
        {
            EpochLength = EpochLength,
            Gap = Gap,
            SwitchBlock = 0,
            MergeSignRange = MergeSignRange,
            RangeReturnSigner = RangeReturnSigner,
            BlockSignerContract = new Address("0x0000000000000000000000000000000000000089"),
            V2Configs = [new V2ConfigParams()]
        };

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(releaseSpec);

        IEpochSwitchManager epochSwitchManager = Substitute.For<IEpochSwitchManager>();
        epochSwitchManager.IsEpochSwitchAtBlock(Arg.Any<XdcBlockHeader>())
            .Returns(ci => ((XdcBlockHeader)ci.Args()[0]).Number % EpochLength == 0);
        epochSwitchManager.GetEpochSwitchInfo(Arg.Any<XdcBlockHeader>())
            .Returns(ci =>
            {
                XdcBlockHeader header = (XdcBlockHeader)ci.Args()[0];
                ulong switchEpoch = header.Number / EpochLength * EpochLength;
                int idx = Math.Min((int)switchEpoch, chainLength - 1);
                return new EpochSwitchInfo(
                    validatorAddresses,
                    [],
                    headers[idx].PenaltiesAddress?.ToArray() ?? [],
                    new BlockRoundInfo(headers[idx].Hash!, switchEpoch, switchEpoch));
            });

        ISigningTxCache signingTxCache = new SigningTxCache(blockTree, specProvider);
        SubnetPenaltyHandler handler = new(blockTree, specProvider, epochSwitchManager, signingTxCache);

        return new MockedSubnetPenaltyContext(
            headers, validatorKeys, releaseSpec, hashToBlock, handler);
    }

    private sealed record MockedSubnetPenaltyContext(
        XdcSubnetBlockHeader[] Headers,
        PrivateKey[] ValidatorKeys,
        IXdcReleaseSpec Spec,
        Dictionary<Hash256, Block> HashToBlock,
        SubnetPenaltyHandler Handler);
}
