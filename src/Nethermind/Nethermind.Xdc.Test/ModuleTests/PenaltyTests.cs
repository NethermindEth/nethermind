// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Test.Helpers;
using Nethermind.Xdc.Types;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Xdc.Test.ModuleTests;

internal class PenaltyTests
{
    private const int EpochLength = 90;
    private const long MergeSignRange = 15;
    private const int TestMasternodeCount = 20;

    [Test]
    public async Task TestHookPenaltyV2Comeback()
    {
        XdcTestBlockchain chain = await XdcTestBlockchain.Create(blocksToAdd: 0, withPenalty: true);
        ConfigureFastPenaltySpec(chain);

        PenaltyHandler penaltyHandler = CreatePenaltyHandler(chain);
        ISigningTxCache signingTxCache = chain.Container.Resolve<ISigningTxCache>();
        IXdcReleaseSpec spec = chain.SpecProvider.GetXdcSpec((XdcBlockHeader)chain.BlockTree.Head!.Header);
        await chain.AddBlocks(spec.EpochLength * 3);

        spec = chain.SpecProvider.GetXdcSpec((XdcBlockHeader)chain.BlockTree.Head!.Header);
        long epoch = spec.EpochLength;
        long targetHeight = spec.SwitchBlock + epoch * 3;

        XdcBlockHeader firstEpochHeader = (XdcBlockHeader)chain.BlockTree.FindHeader(spec.SwitchBlock + epoch + 1)!;
        EpochSwitchInfo? epochInfo = chain.EpochSwitchManager.GetEpochSwitchInfo(firstEpochHeader);
        Assert.That(epochInfo, Is.Not.Null);
        Address[] masternodes = epochInfo.Masternodes;

        XdcBlockHeader targetHeader = (XdcBlockHeader)chain.BlockTree.FindHeader(targetHeight)!;
        Address[] penalties = penaltyHandler.HandlePenalties(targetHeader.Number, targetHeader.ParentHash!, masternodes);
        Assert.That(penalties, Has.Length.EqualTo(1));
        PrivateKey penaltyHistorySigner = GetPenaltyHistorySigner(chain, penalties[0]);

        XdcBlockHeader signedHeader = (XdcBlockHeader)chain.BlockTree.FindHeader(targetHeader.Number - spec.MergeSignRange)!;
        Transaction tx = BuildSigningTx(spec, signedHeader.Number, signedHeader.Hash!, penaltyHistorySigner);
        signingTxCache.SetSigningTransactions(signedHeader.Hash!, [tx]);

        penalties = penaltyHandler.HandlePenalties(targetHeader.Number, targetHeader.ParentHash!, masternodes);
        Assert.That(penalties, Is.Empty);
    }

    [Test]
    public async Task TestHookPenaltyV2Jump()
    {
        XdcTestBlockchain chain = await XdcTestBlockchain.Create(blocksToAdd: 0, withPenalty: true);
        ConfigureFastPenaltySpec(chain);

        PenaltyHandler penaltyHandler = CreatePenaltyHandler(chain);
        IXdcReleaseSpec spec = chain.SpecProvider.GetXdcSpec((XdcBlockHeader)chain.BlockTree.Head!.Header);
        await chain.AddBlocks(spec.EpochLength * 3);

        spec = chain.SpecProvider.GetXdcSpec((XdcBlockHeader)chain.BlockTree.Head!.Header);
        long epoch = spec.EpochLength;
        long targetHeight = spec.SwitchBlock + epoch * 3 - spec.MergeSignRange;

        XdcBlockHeader firstEpochHeader = (XdcBlockHeader)chain.BlockTree.FindHeader(spec.SwitchBlock + epoch + 1)!;
        Address[] masternodes = chain.EpochSwitchManager.GetEpochSwitchInfo(firstEpochHeader)!.Masternodes;

        XdcBlockHeader targetHeader = (XdcBlockHeader)chain.BlockTree.FindHeader(targetHeight)!;

        Address[] penalties = penaltyHandler.HandlePenalties(targetHeader.Number, targetHeader.ParentHash!, masternodes);
        Assert.That(penalties, Has.Length.EqualTo(1));
    }

    [Test]
    public async Task TestGetPenalties()
    {
        XdcTestBlockchain chain = await XdcTestBlockchain.Create(blocksToAdd: 0, withPenalty: true);
        ConfigureFastPenaltySpec(chain);
        PenaltyHandler penaltyHandler = CreatePenaltyHandler(chain);
        IXdcReleaseSpec spec = chain.SpecProvider.GetXdcSpec((XdcBlockHeader)chain.BlockTree.Head!.Header);
        await chain.AddBlocks(spec.EpochLength * 3);

        spec = chain.SpecProvider.GetXdcSpec((XdcBlockHeader)chain.BlockTree.Head!.Header);
        long epoch = spec.EpochLength;

        XdcBlockHeader headerBeforeThirdEpochSwitch = (XdcBlockHeader)chain.BlockTree.FindHeader(spec.SwitchBlock + epoch * 3 - 1)!;
        XdcBlockHeader headerAfterSecondEpochSwitch = (XdcBlockHeader)chain.BlockTree.FindHeader(spec.SwitchBlock + epoch * 2 + 1)!;

        Assert.That(penaltyHandler.GetPenalties(headerBeforeThirdEpochSwitch), Has.Length.EqualTo(1));
        Assert.That(penaltyHandler.GetPenalties(headerAfterSecondEpochSwitch), Has.Length.EqualTo(1));
    }

    [Test]
    public void TestHookPenaltyParolee()
    {
        MockedPenaltyContext context = CreateMockedPenaltyContext(
            targetEpoch: EpochLength * 3L,
            limitPenaltyEpoch: 2,
            shouldPenalizeAtSwitch: _ => true);
        long secondEpoch = EpochLength * 2L;
        long thirdEpoch = EpochLength * 3L;

        // Parole logic is not yet active, only the "not mining enough" penalty applies
        Address[] penaltiesAtSecondEpoch = context.PenaltyHandler.HandlePenalties(secondEpoch, context.BlockHeaders[(int)(secondEpoch - 1)].Hash!, context.MasternodesAddress);
        Assert.That(penaltiesAtSecondEpoch, Has.Length.EqualTo(1));
        Assert.That(penaltiesAtSecondEpoch[0], Is.EqualTo(context.MasternodesAddress.Last()));

        // Parole logic runs: `signer` has been penalized for 2 epochs but has 0 signing txs
        // Fails parole, so signer stays penalized
        Address[] penaltiesAtThirdEpoch = context.PenaltyHandler.HandlePenalties(thirdEpoch, context.BlockHeaders[(int)(thirdEpoch - 1)].Hash!, context.MasternodesAddress);
        Assert.That(penaltiesAtThirdEpoch, Has.Length.EqualTo(2));

        // Insert signing tx into cache
        // Signer still has only 1 signing tx, so still fails parole
        CacheSigningTxAt(context, thirdEpoch - MergeSignRange);
        penaltiesAtThirdEpoch = context.PenaltyHandler.HandlePenalties(thirdEpoch, context.BlockHeaders[(int)(thirdEpoch - 1)].Hash!, context.MasternodesAddress);
        Assert.That(penaltiesAtThirdEpoch, Has.Length.EqualTo(2));

        // Insert another signing transaction
        // Signer now has 2 signing txs and has been penalized for 2 epochs
        // Parole conditions are met; signer is removed from penalties
        CacheSigningTxAt(context, thirdEpoch - MergeSignRange * 2, nonce: 1);
        penaltiesAtThirdEpoch = context.PenaltyHandler.HandlePenalties(thirdEpoch, context.BlockHeaders[(int)(thirdEpoch - 1)].Hash!, context.MasternodesAddress);
        Assert.That(penaltiesAtThirdEpoch, Has.Length.EqualTo(1));
    }

    [Test]
    public void TestHookPenaltyParoleeCustomized()
    {
        MockedPenaltyContext context = CreateMockedPenaltyContext(
            targetEpoch: EpochLength * 7L,
            limitPenaltyEpoch: 4,
            shouldPenalizeAtSwitch: switchBlock => switchBlock != EpochLength * 4L);
        long targetEpoch = EpochLength * 7L;

        CacheSigningTxAt(context, targetEpoch - MergeSignRange);
        CacheSigningTxAt(context, targetEpoch - MergeSignRange * 2, nonce: 1);

        Address[] penalties = context.PenaltyHandler.HandlePenalties(targetEpoch, context.BlockHeaders[(int)(targetEpoch - 1)].Hash!, context.MasternodesAddress);
        // Signer is not parolee due to one-epoch gap (at 360), plus one stable non-mining masternode.
        Assert.That(penalties, Has.Length.EqualTo(2));
    }

    private static PenaltyHandler CreatePenaltyHandler(XdcTestBlockchain chain)
    {
        ISigningTxCache signingTxCache = chain.Container.Resolve<ISigningTxCache>();
        return new PenaltyHandler(chain.BlockTree, chain.SpecProvider, chain.EpochSwitchManager, signingTxCache);
    }

    private static void ConfigureFastPenaltySpec(XdcTestBlockchain chain, bool activatePenaltyUpgrade = false)
    {
        chain.ChangeReleaseSpec(spec =>
        {
            spec.EpochLength = EpochLength;
            spec.IsTipUpgradePenaltyEnabled = activatePenaltyUpgrade;
            spec.RangeReturnSigner = 150;
            spec.LimitPenaltyEpoch = 2;
        });
    }

    private static PrivateKey GetPenaltyHistorySigner(XdcTestBlockchain chain, Address penaltyAddress)
    {
        return chain.MasterNodeCandidates.First(k => k.Address == penaltyAddress);
    }

    private static Transaction BuildSigningTx(IXdcReleaseSpec spec, long blockNumber, Hash256 blockHash, PrivateKey signer, long nonce = 0)
    {
        return Build.A.Transaction
            .WithChainId(0)
            .WithNonce((UInt256)nonce)
            .WithGasLimit(200000)
            .WithXdcSigningData(blockNumber, blockHash)
            .ToBlockSignerContract(spec)
            .SignedAndResolved(signer)
            .TestObject;
    }

    private static MockedPenaltyContext CreateMockedPenaltyContext(
        long targetEpoch,
        int limitPenaltyEpoch,
        System.Func<long, bool> shouldPenalizeAtSwitch)
    {
        PrivateKey[] masternodes = XdcTestHelper.GeneratePrivateKeys(TestMasternodeCount);
        Address[] masternodesAddress = masternodes.Select(privateKey => privateKey.Address).ToArray();
        PrivateKey penaltySigner = masternodes.First();

        IBlockTree blockTree = Substitute.For<IBlockTree>();
        int chainSize = (int)(targetEpoch + 1);

        var blockHeaders = new XdcBlockHeader[chainSize];
        var hashToHeader = new Dictionary<Hash256, XdcBlockHeader>();
        var blocks = new Block[chainSize];

        for (int i = 0; i < chainSize; i++)
        {
            Hash256 parentHash = i == 0 ? Hash256.Zero : blockHeaders[i - 1].Hash!;
            blockHeaders[i] = Build.A.XdcBlockHeader()
                .WithNumber(i)
                .WithParentHash(parentHash)
                .WithValidators(masternodesAddress)
                .WithExtraConsensusData(new ExtraFieldsV2((ulong)i, Build.A.QuorumCertificate().TestObject))
                .WithPenalties([penaltySigner.Address])
                .TestObject;

            blockHeaders[i].Beneficiary = masternodes[i % (masternodes.Length - 1)].Address;
            if (!shouldPenalizeAtSwitch(i)) blockHeaders[i].Penalties = [];
            Hash256 hash = blockHeaders[i].Hash ?? blockHeaders[i].CalculateHash().ToHash256();
            hashToHeader[hash] = blockHeaders[i];
            blocks[i] = new Block(blockHeaders[i]);
        }

        blockTree.FindHeader(Arg.Any<Hash256>(), Arg.Any<long>())
            .Returns(ci => hashToHeader[ci.ArgAt<Hash256>(0)]);
        blockTree.Head.Returns(blocks.Last());

        IXdcReleaseSpec xdcSpec = Substitute.For<IXdcReleaseSpec>();
        xdcSpec.EpochLength.Returns(EpochLength);
        xdcSpec.SwitchBlock.Returns(0);
        xdcSpec.MergeSignRange.Returns(MergeSignRange);
        xdcSpec.IsTipUpgradePenaltyEnabled.Returns(true);
        xdcSpec.LimitPenaltyEpoch.Returns(limitPenaltyEpoch);
        xdcSpec.MinimumSigningTx.Returns(2);
        xdcSpec.MinimumMinerBlockPerEpoch.Returns(1);

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(xdcSpec);

        IEpochSwitchManager epochSwitchManager = Substitute.For<IEpochSwitchManager>();
        epochSwitchManager.IsEpochSwitchAtBlock(Arg.Any<XdcBlockHeader>())
            .Returns(ci => ((XdcBlockHeader)ci.Args()[0]).Number % EpochLength == 0);
        epochSwitchManager.GetEpochSwitchInfo(Arg.Any<Hash256>()).Returns(ci =>
        {
            XdcBlockHeader header = hashToHeader[(Hash256)ci.Args()[0]];
            long switchEpoch = header.Number / EpochLength * EpochLength;
            return new EpochSwitchInfo(
                masternodesAddress,
                [],
                blockHeaders[(int)switchEpoch].PenaltiesAddress?.ToArray() ?? [],
                new BlockRoundInfo(blockHeaders[(int)switchEpoch].Hash!, (ulong)switchEpoch, switchEpoch));
        });
        epochSwitchManager.GetBlockByEpochNumber(Arg.Any<ulong>()).Returns(ci =>
        {
            long blockNumber = EpochLength * (long)(ulong)ci.Args()[0];
            XdcBlockHeader header = blockHeaders[(int)blockNumber];
            return new BlockRoundInfo(header.Hash!, (ulong)blockNumber, blockNumber);
        });

        ISigningTxCache signingTxCache = new SigningTxCache(blockTree, specProvider);
        PenaltyHandler penaltyHandler = new(blockTree, specProvider, epochSwitchManager, signingTxCache);
        return new MockedPenaltyContext(blockHeaders, masternodesAddress, penaltySigner, xdcSpec, signingTxCache, penaltyHandler);
    }

    private static void CacheSigningTxAt(MockedPenaltyContext context, long signedBlockNumber, long nonce = 0)
    {
        XdcBlockHeader signedHeader = context.BlockHeaders[(int)signedBlockNumber];
        context.SigningTxCache.SetSigningTransactions(
            signedHeader.Hash!,
            [BuildSigningTx(context.Spec, signedHeader.Number, signedHeader.Hash!, context.PenaltySigner, nonce)]);
    }

    private sealed record MockedPenaltyContext(
        XdcBlockHeader[] BlockHeaders,
        Address[] MasternodesAddress,
        PrivateKey PenaltySigner,
        IXdcReleaseSpec Spec,
        ISigningTxCache SigningTxCache,
        PenaltyHandler PenaltyHandler);
}
