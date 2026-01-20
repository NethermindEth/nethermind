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

    [Test]
    public void Block_With_Invalid_Qc_Fails()
    {
        // test case needs reverification of what actually is going on (this is only a draft for now)

        var invalidRoundBlock = GetLastHeader(false);
        var invalidRoundBlockParent = (XdcBlockHeader)xdcTestBlockchain.BlockTree.FindHeader(invalidRoundBlock.ParentHash!)!;

        var proposedBlockInfo = new BlockRoundInfo(invalidRoundBlockParent.Hash!, invalidRoundBlockParent.ExtraConsensusData!.BlockRound, invalidRoundBlockParent.Number);

        var voteForSign = new Vote(proposedBlockInfo, 1);

        var validSigners = xdcTestBlockchain.MasterNodeCandidates
            .Where(pvKey => invalidRoundBlockParent.ValidatorsAddress!.Value.Contains(pvKey.Address))
            .Select(pvKey => new Signer(0, pvKey, xdcTestBlockchain.LogManager))
            .ToList();

        List<Signature> signatures = [];
        foreach (var signer in validSigners)
        {
            Sign(voteForSign, signer);
            signatures.Add(voteForSign.Signature!);
        }

        var quorumCert = new QuorumCertificate(proposedBlockInfo, signatures.ToArray(), 1);

        var extra = new ExtraFieldsV2(proposedBlockInfo.Round, quorumCert);
        var extraInBytes = extraConsensusDataDecoder.Encode(extra).Bytes;

        invalidRoundBlock.ExtraData = extraInBytes;
        var result = xdcHeaderValidator.Validate(invalidRoundBlock, invalidRoundBlockParent);
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task Block_With_Illegitimate_Signer_Fails()
    {
        var previousSigner = xdcSigner.Key;

        var coinbaseValidatorMismatchBlock = GetLastBlock(false);
        var coinbaseValidatorMismatchBlockParent = xdcTestBlockchain.BlockTree.FindHeader(coinbaseValidatorMismatchBlock.ParentHash!);

        var notQualifiedSigner = TestItem.PrivateKeyA; // private key
        ((Signer)xdcSigner).SetSigner(notQualifiedSigner);
        await xdcTestBlockchain.SealEngine.SealBlock(coinbaseValidatorMismatchBlock, default);

        ((Signer)xdcSigner).SetSigner(previousSigner);
        var result = xdcHeaderValidator.Validate(coinbaseValidatorMismatchBlock.Header, coinbaseValidatorMismatchBlockParent!);
        Assert.That(result, Is.False);
    }

    [Test]
    public void EpochSwitchBlock_With_NotLegit_PenaltiesSet_Fails()
    {
        var penaltiesNotLegit = GetLastHeader(true);
        var penaltiesNotLegitParent = xdcTestBlockchain.BlockTree.FindHeader(penaltiesNotLegit.ParentHash!);
        penaltiesNotLegit.Penalties = [.. penaltiesNotLegit.Penalties!, .. TestItem.AddressA.Bytes];
        var result = xdcHeaderValidator.Validate(penaltiesNotLegit, penaltiesNotLegitParent!);
        Assert.That(result, Is.False);
    }

    [Test]
    public void EpochSwitchBlock_With_NotLegit_ValidatorsSet_Fails()
    {
        var validatorsNotLegit = GetLastHeader(true);
        var validatorsNotLegitParent = xdcTestBlockchain.BlockTree.FindHeader(validatorsNotLegit.ParentHash!);
        validatorsNotLegit.Validators = [.. validatorsNotLegit.Validators!, .. TestItem.AddressA.Bytes];
        var result = xdcHeaderValidator.Validate(validatorsNotLegit, validatorsNotLegitParent!);
        Assert.That(result, Is.False);
    }

    [Test]
    public void EpochSwitchBlock_With_Invalid_ValidatorsSet_Fails()
    {
        var invalidValidatorsSignerBlock = GetLastHeader(true);
        var invalidValidatorsSignerBlockParent = xdcTestBlockchain.BlockTree.FindHeader(invalidValidatorsSignerBlock.ParentHash!);
        invalidValidatorsSignerBlock.Validators = [123];
        var result = xdcHeaderValidator.Validate(invalidValidatorsSignerBlock, invalidValidatorsSignerBlockParent!);
        Assert.That(result, Is.False);
    }

    [Test]
    public void EpochSwitchBlock_With_Invalid_Nonce_Fails()
    {
        var invalidAuthNonceBlock = GetLastHeader(true);
        var invalidAuthNonceBlockParent = xdcTestBlockchain.BlockTree.FindHeader(invalidAuthNonceBlock.ParentHash!);
        invalidAuthNonceBlock.Nonce = 123;
        var result = xdcHeaderValidator.Validate(invalidAuthNonceBlock, invalidAuthNonceBlockParent!);
        Assert.That(result, Is.False);
    }

    [Test]
    public void Block_Mined_TooFast_After_Parent_Fails()
    {
        var tooFastMinedBlock = GetLastHeader(false);
        var tooFastMinedBlockParent = xdcTestBlockchain.BlockTree.FindHeader(tooFastMinedBlock.ParentHash!);
        tooFastMinedBlock.Timestamp = (ulong)(tooFastMinedBlockParent!.Timestamp + 1); // mined 1 second after parent
        var result = xdcHeaderValidator.Validate(tooFastMinedBlock, tooFastMinedBlockParent);
        Assert.That(result, Is.False);
    }

    [Test]
    public void Block_With_Invalid_Difficulty_Fails()
    {
        var invalidDifficultyBlock = GetLastHeader(false);
        var invalidDifficultyBlockParent = xdcTestBlockchain.BlockTree.FindHeader(invalidDifficultyBlock.ParentHash!);
        invalidDifficultyBlock.Difficulty = 2;
        var result = xdcHeaderValidator.Validate(invalidDifficultyBlock, invalidDifficultyBlockParent!);
        Assert.That(result, Is.False);
    }

    [Test]
    public void EpochSwitchBlock_With_Empty_ValidatorSet_Fails()
    {
        var emptyValidatorsBlock = GetLastHeader(true);
        var emptyValidatorsBlockParent = xdcTestBlockchain.BlockTree.FindHeader(emptyValidatorsBlock.ParentHash!);
        emptyValidatorsBlock.Validators = [];
        var result = xdcHeaderValidator.Validate(emptyValidatorsBlock, emptyValidatorsBlockParent!);
        Assert.That(result, Is.False);
    }

    [Test]
    public void Block_With_InvalidParent_Fails()
    {
        var parentNotExistBlock = GetLastHeader(true);
        parentNotExistBlock.ParentHash = TestItem.KeccakA;
        var parentNotExistBlockParent = xdcTestBlockchain.BlockTree.FindHeader(parentNotExistBlock.ParentHash!);
        Assert.Throws<ArgumentNullException>(() => xdcHeaderValidator.Validate(parentNotExistBlock, parentNotExistBlockParent!));
    }

    [Test]
    public void NonEpochBlock_With_Penalties_Fails()
    {
        var invalidPenaltiesExistBlock = GetLastHeader(false);
        invalidPenaltiesExistBlock.Penalties = TestItem.AddressF.Bytes;
        var invalidPenaltiesExistBlockParent = xdcTestBlockchain.BlockTree.FindHeader(invalidPenaltiesExistBlock.ParentHash!);
        var result = xdcHeaderValidator.Validate(invalidPenaltiesExistBlock, invalidPenaltiesExistBlockParent!);
        Assert.That(result, Is.False);
    }

    [Test]
    public void NonEpochBlock_Invalid_ValidatorsSet_Fails()
    {
        var invalidValidatorsExistBlock = GetLastHeader(false);
        var invalidValidatorsExistBlockParent = xdcTestBlockchain.BlockTree.FindHeader(invalidValidatorsExistBlock.ParentHash!);
        invalidValidatorsExistBlock.Validators = [123];
        var result = xdcHeaderValidator.Validate(invalidValidatorsExistBlock, invalidValidatorsExistBlockParent!);
        Assert.That(result, Is.False);
    }

    [Test]
    public void Block_With_Invalid_QcExtra_Fails()
    {
        var invalidQcBlock = GetLastHeader(false);
        var invalidQcBlockParent = xdcTestBlockchain.BlockTree.FindHeader(invalidQcBlock.ParentHash!);
        invalidQcBlock.ExtraData = [(byte)Random.Shared.Next()];
        var result = xdcHeaderValidator.Validate(invalidQcBlock, invalidQcBlockParent!);
        Assert.That(result, Is.False);
    }

    [Test]
    public void Block_From_Future_Fails()
    {
        var blockFromFutureBlock = GetLastHeader(false);
        var blockFromFutureBlockParent = xdcTestBlockchain.BlockTree.FindHeader(blockFromFutureBlock.ParentHash!);
        blockFromFutureBlock.Timestamp = (ulong)DateTime.UtcNow.ToUnixTimeSeconds() + 10000;
        var result = xdcHeaderValidator.Validate(blockFromFutureBlock, blockFromFutureBlockParent!);
        Assert.That(result, Is.False);
    }

    [Test]
    public void Block_Lacks_ValidatorField_Fails()
    {
        var noValidatorBlock = GetLastHeader(false);
        var noValidatorBlockParent = xdcTestBlockchain.BlockTree.FindHeader(noValidatorBlock.ParentHash!);
        noValidatorBlock.Validator = []; // empty
        var result = xdcHeaderValidator.Validate(noValidatorBlock, noValidatorBlockParent!);
        Assert.That(result, Is.False);
    }

    [Test]
    public void NonEpochSwitch_Block_With_ValidatorsSet()
    {
        var nonEpochSwitchWithValidators = GetLastHeader(false);
        nonEpochSwitchWithValidators.Validators = xdcTestBlockchain.MasterNodeCandidates.SelectMany(addr => addr.Address.Bytes).ToArray(); // implement helper to return acc1 addr bytes
        var nonEpochSwitchWithValidatorsParent = xdcTestBlockchain.BlockTree.FindHeader(nonEpochSwitchWithValidators.ParentHash!);
        var result = xdcHeaderValidator.Validate(nonEpochSwitchWithValidators, nonEpochSwitchWithValidatorsParent);
        Assert.That(result, Is.False);
    }

    [Test]
    public void Valid_EpochSwitch_Block_Passes_Validation()
    {
        var happyPathHeader = GetLastHeader(true);
        var happyPathParent = xdcTestBlockchain.BlockTree.FindHeader(happyPathHeader.ParentHash!);
        var result = xdcHeaderValidator.Validate(happyPathHeader, happyPathParent);
        Assert.That(result, Is.True);
    }

    [Test]
    public void Valid_NonEpochSwitch_Block_Passes_Validation()
    {
        var happyPathHeader = GetLastHeader(false);
        var happyPathParent = xdcTestBlockchain.BlockTree.FindHeader(happyPathHeader.ParentHash!);

        var result = xdcHeaderValidator.Validate(happyPathHeader, happyPathParent);
        Assert.That(result, Is.True);
    }

    [Test]
    public void Block_With_QcSignature_Below_Threshold_Fails()
    {
        var invalidQcSignatureBlock = GetLastHeader(false);
        var invalidQcSignatureBlockParent = (XdcBlockHeader)xdcTestBlockchain.BlockTree.FindHeader(invalidQcSignatureBlock.ParentHash!)!;
        var proposedBlockInfo = new BlockRoundInfo(invalidQcSignatureBlockParent!.Hash!, invalidQcSignatureBlockParent.ExtraConsensusData!.BlockRound, invalidQcSignatureBlockParent.Number);
        var voteForSign = new Vote(proposedBlockInfo, 1);
        var validSigners = xdcTestBlockchain.MasterNodeCandidates
            .Where(pvKey => invalidQcSignatureBlockParent.ValidatorsAddress!.Value.Contains(pvKey.Address))
            .Select(pvKey => new Signer(0, pvKey, xdcTestBlockchain.LogManager))
            .ToList();
        List<Signature> signatures = [];

        double threshold = xdcTestBlockchain.SpecProvider.GetXdcSpec(invalidQcSignatureBlock).CertThreshold;

        // Sign with only half of the valid signers to be below threshold
        foreach (var signer in validSigners.Take((int)threshold - 1))
        {
            Sign(voteForSign, signer);
            signatures.Add(voteForSign.Signature!);
        }

        var quorumCert = new QuorumCertificate(proposedBlockInfo, signatures.ToArray(), 1);
        var extra = new ExtraFieldsV2(proposedBlockInfo.Round, quorumCert);
        var extraInBytes = extraConsensusDataDecoder.Encode(extra).Bytes;
        invalidQcSignatureBlock.ExtraData = extraInBytes;
        var result = xdcHeaderValidator.Validate(invalidQcSignatureBlock, invalidQcSignatureBlockParent);
        Assert.That(result, Is.False);
    }

    private void Sign(Vote vote, Consensus.ISigner signer)
    {
        var voteEncoder = new VoteDecoder();
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
            var currentHeader = (XdcBlockHeader)xdcTestBlockchain.BlockTree.Head!.Header;
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
        var header = GetLastHeader(isEpochSwitch);
        var block = xdcTestBlockchain.BlockTree.FindBlock(header.Hash!);
        if (block is null)
        {
            throw new InvalidOperationException("Block not found in the chain.");
        }

        return block;
    }
}
