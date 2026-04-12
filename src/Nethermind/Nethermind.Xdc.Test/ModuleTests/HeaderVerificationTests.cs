// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Consensus;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.RLP;
using Nethermind.Xdc.Test.Helpers;
using Nethermind.Xdc.Types;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ISigner = Nethermind.Consensus.ISigner;

namespace Nethermind.Xdc.Test.ModuleTests;

internal class HeaderVerificationTests
{
    private XdcTestBlockchain xdcTestBlockchain;
    private IHeaderValidator xdcHeaderValidator;
    private ISigner xdcSigner;
    private ExtraConsensusDataDecoder extraConsensusDataDecoder;

    [SetUp]
    public async Task Setup()
    {
        xdcTestBlockchain = await XdcTestBlockchain.Create();
        xdcHeaderValidator = xdcTestBlockchain.Container.Resolve<IHeaderValidator>();
        xdcSigner = xdcTestBlockchain.Container.Resolve<ISigner>();
        extraConsensusDataDecoder = new();
    }

    [TearDown]
    public void TearDown()
    {
        xdcTestBlockchain?.Dispose();
    }

    [Test]
    public void Block_With_Invalid_Qc_Fails()
    {
        // test case needs reverification of what actually is going on (this is only a draft for now)

        XdcBlockHeader invalidRoundBlock = GetLastHeader(false);
        XdcBlockHeader invalidRoundBlockParent = (XdcBlockHeader)xdcTestBlockchain.BlockTree.FindHeader(invalidRoundBlock.ParentHash!)!;

        BlockRoundInfo proposedBlockInfo = new(invalidRoundBlockParent.Hash!, invalidRoundBlockParent.ExtraConsensusData!.BlockRound, invalidRoundBlockParent.Number);

        Vote voteForSign = new(proposedBlockInfo, 1);

        List<Signer> validSigners = xdcTestBlockchain.MasterNodeCandidates
            .Where(pvKey => invalidRoundBlockParent.ValidatorsAddress!.Value.Contains(pvKey.Address))
            .Select(pvKey => new Signer(0, pvKey, xdcTestBlockchain.LogManager))
            .ToList();

        List<Signature> signatures = [];
        foreach (Signer? signer in validSigners)
        {
            Sign(voteForSign, signer);
            signatures.Add(voteForSign.Signature!);
        }

        QuorumCertificate quorumCert = new(proposedBlockInfo, signatures.ToArray(), 1);

        ExtraFieldsV2 extra = new(proposedBlockInfo.Round, quorumCert);
        byte[] extraInBytes = extraConsensusDataDecoder.Encode(extra).Bytes;

        invalidRoundBlock.ExtraData = extraInBytes;
        bool result = xdcHeaderValidator.Validate(invalidRoundBlock, invalidRoundBlockParent);
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task Block_With_Illegitimate_Signer_Fails()
    {
        PrivateKey? previousSigner = xdcSigner.Key;

        Block coinbaseValidatorMismatchBlock = GetLastBlock(false);
        BlockHeader? coinbaseValidatorMismatchBlockParent = xdcTestBlockchain.BlockTree.FindHeader(coinbaseValidatorMismatchBlock.ParentHash!);

        PrivateKey notQualifiedSigner = TestItem.PrivateKeyA; // private key
        ((Signer)xdcSigner).SetSigner(notQualifiedSigner);
        await xdcTestBlockchain.SealEngine.SealBlock(coinbaseValidatorMismatchBlock, default);

        ((Signer)xdcSigner).SetSigner(previousSigner);
        bool result = xdcHeaderValidator.Validate(coinbaseValidatorMismatchBlock.Header, coinbaseValidatorMismatchBlockParent!);
        Assert.That(result, Is.False);
    }

    [Test]
    public void EpochSwitchBlock_With_NotLegit_PenaltiesSet_Fails()
    {
        XdcBlockHeader penaltiesNotLegit = GetLastHeader(true);
        BlockHeader? penaltiesNotLegitParent = xdcTestBlockchain.BlockTree.FindHeader(penaltiesNotLegit.ParentHash!);
        penaltiesNotLegit.Penalties = [.. penaltiesNotLegit.Penalties!, .. TestItem.AddressA.Bytes];
        bool result = xdcHeaderValidator.Validate(penaltiesNotLegit, penaltiesNotLegitParent!);
        Assert.That(result, Is.False);
    }

    [Test]
    public void EpochSwitchBlock_With_NotLegit_ValidatorsSet_Fails()
    {
        XdcBlockHeader validatorsNotLegit = GetLastHeader(true);
        BlockHeader? validatorsNotLegitParent = xdcTestBlockchain.BlockTree.FindHeader(validatorsNotLegit.ParentHash!);
        validatorsNotLegit.Validators = [.. validatorsNotLegit.Validators!, .. TestItem.AddressA.Bytes];
        bool result = xdcHeaderValidator.Validate(validatorsNotLegit, validatorsNotLegitParent!);
        Assert.That(result, Is.False);
    }

    [Test]
    public void EpochSwitchBlock_With_Invalid_ValidatorsSet_Fails()
    {
        XdcBlockHeader invalidValidatorsSignerBlock = GetLastHeader(true);
        BlockHeader? invalidValidatorsSignerBlockParent = xdcTestBlockchain.BlockTree.FindHeader(invalidValidatorsSignerBlock.ParentHash!);
        invalidValidatorsSignerBlock.Validators = [123];
        bool result = xdcHeaderValidator.Validate(invalidValidatorsSignerBlock, invalidValidatorsSignerBlockParent!);
        Assert.That(result, Is.False);
    }

    [Test]
    public void EpochSwitchBlock_With_Invalid_Nonce_Fails()
    {
        XdcBlockHeader invalidAuthNonceBlock = GetLastHeader(true);
        BlockHeader? invalidAuthNonceBlockParent = xdcTestBlockchain.BlockTree.FindHeader(invalidAuthNonceBlock.ParentHash!);
        invalidAuthNonceBlock.Nonce = 123;
        bool result = xdcHeaderValidator.Validate(invalidAuthNonceBlock, invalidAuthNonceBlockParent!);
        Assert.That(result, Is.False);
    }

    [Test]
    public void Block_Mined_TooFast_After_Parent_Fails()
    {
        XdcBlockHeader tooFastMinedBlock = GetLastHeader(false);
        BlockHeader? tooFastMinedBlockParent = xdcTestBlockchain.BlockTree.FindHeader(tooFastMinedBlock.ParentHash!);
        tooFastMinedBlock.Timestamp = (ulong)(tooFastMinedBlockParent!.Timestamp + 1); // mined 1 second after parent
        bool result = xdcHeaderValidator.Validate(tooFastMinedBlock, tooFastMinedBlockParent);
        Assert.That(result, Is.False);
    }

    [Test]
    public void Block_With_Invalid_Difficulty_Fails()
    {
        XdcBlockHeader invalidDifficultyBlock = GetLastHeader(false);
        BlockHeader? invalidDifficultyBlockParent = xdcTestBlockchain.BlockTree.FindHeader(invalidDifficultyBlock.ParentHash!);
        invalidDifficultyBlock.Difficulty = 2;
        bool result = xdcHeaderValidator.Validate(invalidDifficultyBlock, invalidDifficultyBlockParent!);
        Assert.That(result, Is.False);
    }

    [Test]
    public void EpochSwitchBlock_With_Empty_ValidatorSet_Fails()
    {
        XdcBlockHeader emptyValidatorsBlock = GetLastHeader(true);
        BlockHeader? emptyValidatorsBlockParent = xdcTestBlockchain.BlockTree.FindHeader(emptyValidatorsBlock.ParentHash!);
        emptyValidatorsBlock.Validators = [];
        bool result = xdcHeaderValidator.Validate(emptyValidatorsBlock, emptyValidatorsBlockParent!);
        Assert.That(result, Is.False);
    }

    [Test]
    public void Block_With_InvalidParent_Fails()
    {
        XdcBlockHeader parentNotExistBlock = GetLastHeader(true);
        parentNotExistBlock.ParentHash = TestItem.KeccakA;
        BlockHeader? parentNotExistBlockParent = xdcTestBlockchain.BlockTree.FindHeader(parentNotExistBlock.ParentHash!);
        Assert.Throws<ArgumentNullException>(() => xdcHeaderValidator.Validate(parentNotExistBlock, parentNotExistBlockParent!));
    }

    [Test]
    public void NonEpochBlock_With_Penalties_Fails()
    {
        XdcBlockHeader invalidPenaltiesExistBlock = GetLastHeader(false);
        invalidPenaltiesExistBlock.Penalties = TestItem.AddressF.Bytes;
        BlockHeader? invalidPenaltiesExistBlockParent = xdcTestBlockchain.BlockTree.FindHeader(invalidPenaltiesExistBlock.ParentHash!);
        bool result = xdcHeaderValidator.Validate(invalidPenaltiesExistBlock, invalidPenaltiesExistBlockParent!);
        Assert.That(result, Is.False);
    }

    [Test]
    public void NonEpochBlock_Invalid_ValidatorsSet_Fails()
    {
        XdcBlockHeader invalidValidatorsExistBlock = GetLastHeader(false);
        BlockHeader? invalidValidatorsExistBlockParent = xdcTestBlockchain.BlockTree.FindHeader(invalidValidatorsExistBlock.ParentHash!);
        invalidValidatorsExistBlock.Validators = [123];
        bool result = xdcHeaderValidator.Validate(invalidValidatorsExistBlock, invalidValidatorsExistBlockParent!);
        Assert.That(result, Is.False);
    }

    [Test]
    public void Block_With_Invalid_QcExtra_Fails()
    {
        XdcBlockHeader invalidQcBlock = GetLastHeader(false);
        BlockHeader? invalidQcBlockParent = xdcTestBlockchain.BlockTree.FindHeader(invalidQcBlock.ParentHash!);
        invalidQcBlock.ExtraData = [(byte)Random.Shared.Next()];
        bool result = xdcHeaderValidator.Validate(invalidQcBlock, invalidQcBlockParent!);
        Assert.That(result, Is.False);
    }

    [Test]
    public void Block_From_Future_Fails()
    {
        XdcBlockHeader blockFromFutureBlock = GetLastHeader(false);
        BlockHeader? blockFromFutureBlockParent = xdcTestBlockchain.BlockTree.FindHeader(blockFromFutureBlock.ParentHash!);
        blockFromFutureBlock.Timestamp = (ulong)DateTime.UtcNow.ToUnixTimeSeconds() + 10000;
        bool result = xdcHeaderValidator.Validate(blockFromFutureBlock, blockFromFutureBlockParent!);
        Assert.That(result, Is.False);
    }

    [Test]
    public void Block_Lacks_ValidatorField_Fails()
    {
        XdcBlockHeader noValidatorBlock = GetLastHeader(false);
        BlockHeader? noValidatorBlockParent = xdcTestBlockchain.BlockTree.FindHeader(noValidatorBlock.ParentHash!);
        noValidatorBlock.Validator = []; // empty
        bool result = xdcHeaderValidator.Validate(noValidatorBlock, noValidatorBlockParent!);
        Assert.That(result, Is.False);
    }

    [Test]
    public void NonEpochSwitch_Block_With_ValidatorsSet()
    {
        XdcBlockHeader nonEpochSwitchWithValidators = GetLastHeader(false);
        nonEpochSwitchWithValidators.Validators = xdcTestBlockchain.MasterNodeCandidates.SelectMany(addr => addr.Address.Bytes).ToArray(); // implement helper to return acc1 addr bytes
        BlockHeader? nonEpochSwitchWithValidatorsParent = xdcTestBlockchain.BlockTree.FindHeader(nonEpochSwitchWithValidators.ParentHash!);
        bool result = xdcHeaderValidator.Validate(nonEpochSwitchWithValidators, nonEpochSwitchWithValidatorsParent);
        Assert.That(result, Is.False);
    }

    [Test]
    public void Valid_EpochSwitch_Block_Passes_Validation()
    {
        XdcBlockHeader happyPathHeader = GetLastHeader(true);
        BlockHeader? happyPathParent = xdcTestBlockchain.BlockTree.FindHeader(happyPathHeader.ParentHash!);
        bool result = xdcHeaderValidator.Validate(happyPathHeader, happyPathParent);
        Assert.That(result, Is.True);
    }

    [Test]
    public void Valid_NonEpochSwitch_Block_Passes_Validation()
    {
        XdcBlockHeader happyPathHeader = GetLastHeader(false);
        BlockHeader? happyPathParent = xdcTestBlockchain.BlockTree.FindHeader(happyPathHeader.ParentHash!);

        bool result = xdcHeaderValidator.Validate(happyPathHeader, happyPathParent);
        Assert.That(result, Is.True);
    }

    [Test]
    public void Block_With_QcSignature_Below_Threshold_Fails()
    {
        XdcBlockHeader invalidQcSignatureBlock = GetLastHeader(false);
        XdcBlockHeader invalidQcSignatureBlockParent = (XdcBlockHeader)xdcTestBlockchain.BlockTree.FindHeader(invalidQcSignatureBlock.ParentHash!)!;
        BlockRoundInfo proposedBlockInfo = new(invalidQcSignatureBlockParent!.Hash!, invalidQcSignatureBlockParent.ExtraConsensusData!.BlockRound, invalidQcSignatureBlockParent.Number);
        Vote voteForSign = new(proposedBlockInfo, 1);
        List<Signer> validSigners = xdcTestBlockchain.MasterNodeCandidates
            .Where(pvKey => invalidQcSignatureBlockParent.ValidatorsAddress!.Value.Contains(pvKey.Address))
            .Select(pvKey => new Signer(0, pvKey, xdcTestBlockchain.LogManager))
            .ToList();
        List<Signature> signatures = [];

        double threshold = xdcTestBlockchain.SpecProvider.GetXdcSpec(invalidQcSignatureBlock).CertificateThreshold;

        // Sign with only half of the valid signers to be below threshold
        foreach (Signer? signer in validSigners.Take((int)threshold - 1))
        {
            Sign(voteForSign, signer);
            signatures.Add(voteForSign.Signature!);
        }

        QuorumCertificate quorumCert = new(proposedBlockInfo, signatures.ToArray(), 1);
        ExtraFieldsV2 extra = new(proposedBlockInfo.Round, quorumCert);
        byte[] extraInBytes = extraConsensusDataDecoder.Encode(extra).Bytes;
        invalidQcSignatureBlock.ExtraData = extraInBytes;
        bool result = xdcHeaderValidator.Validate(invalidQcSignatureBlock, invalidQcSignatureBlockParent);
        Assert.That(result, Is.False);
    }

    private void Sign(Vote vote, Consensus.ISigner signer)
    {
        VoteDecoder voteEncoder = new();
        KeccakRlpStream stream = new();
        voteEncoder.Encode(stream, vote, RlpBehaviors.ForSealing);
        vote.Signature = signer.Sign(stream.GetValueHash());
        vote.Signer = signer.Address;
    }

    private XdcBlockHeader GetLastHeader(bool isEpochSwitch)
    {
        if (!isEpochSwitch)
        {
            return ClearCacheFields((XdcBlockHeader)xdcTestBlockchain.BlockTree.Head!.Header);
        }
        else
        {
            XdcBlockHeader? currentHeader = (XdcBlockHeader)xdcTestBlockchain.BlockTree.Head!.Header;
            while (currentHeader is not null)
            {
                if (xdcTestBlockchain.EpochSwitchManager.IsEpochSwitchAtBlock(currentHeader))
                {
                    return ClearCacheFields(currentHeader);
                }
                currentHeader = (XdcBlockHeader)xdcTestBlockchain.BlockTree.FindHeader(currentHeader.ParentHash!)!;
            }

            throw new InvalidOperationException("No epoch switch block found in the chain.");
        }
    }

    private static XdcBlockHeader ClearCacheFields(XdcBlockHeader header)
    {
        header.Author = null;
        return header;
    }
    private Block GetLastBlock(bool isEpochSwitch)
    {
        XdcBlockHeader header = GetLastHeader(isEpochSwitch);
        Block? block = xdcTestBlockchain.BlockTree.FindBlock(header.Hash!);
        if (block is null)
        {
            throw new InvalidOperationException("Block not found in the chain.");
        }

        return block;
    }
}
