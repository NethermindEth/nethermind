// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc;
using Nethermind.Xdc.RLP;
using Nethermind.Xdc.Types;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.PortableExecutable;

namespace Nethermind.Core.Test.Builders;

public class XdcBlockHeaderBuilder : BlockHeaderBuilder
{
    private XdcBlockHeader XdcTestObjectInternal => (XdcBlockHeader)TestObjectInternal;

    public new XdcBlockHeader TestObject => (XdcBlockHeader)base.TestObject;


    public XdcBlockHeaderBuilder()
    {
        TestObjectInternal = new XdcBlockHeader(
            Keccak.Compute("parent"),
            Keccak.OfAnEmptySequenceRlp,
            Address.Zero,
            UInt256.One,
            1,
            30_000_000,
            1_700_000_000,
            new byte[] { 1, 2, 3 })
        {
            StateRoot = Keccak.EmptyTreeHash,
            TxRoot = Keccak.EmptyTreeHash,
            ReceiptsRoot = Keccak.EmptyTreeHash,
            Bloom = Bloom.Empty,
            GasUsed = 21_000,
            MixHash = Keccak.Compute("mix_hash"),
            Nonce = 0,
            Validators = new byte[20 * 2],
            Validator = new byte[65],
            Penalties = Array.Empty<byte>(),
        };
    }

    public XdcBlockHeaderBuilder WithExtraFieldsV2(ExtraFieldsV2 extraFieldsV2)
    {
        EncodeExtraData(extraFieldsV2);
        return this;
    }

    public XdcBlockHeaderBuilder WithGeneratedExtraConsensusData(int signatureNumber = 72)
    {
        PrivateKeyGenerator keyBuilder = new PrivateKeyGenerator();
        return WithGeneratedExtraConsensusData(Enumerable.Range(0, signatureNumber).Select(i => keyBuilder.Generate()));
    }

    public XdcBlockHeaderBuilder WithGeneratedExtraConsensusData(IEnumerable<PrivateKey> keys)
    {
        QuorumCertificateDecoder qcEncoder = new QuorumCertificateDecoder();
        EthereumEcdsa ecdsa = new EthereumEcdsa(0);
        BlockRoundInfo blockRoundInfo = new BlockRoundInfo(Hash256.Zero, 1, 1);
        QuorumCert quorumForSigning = new QuorumCert(blockRoundInfo, null, 450);
        IEnumerable<Signature> signatures = keys.Select(k => ecdsa.Sign(k, Keccak.Compute(qcEncoder.Encode(quorumForSigning, RlpBehaviors.ForSealing).Bytes)));
        QuorumCert quorumCert = new QuorumCert(blockRoundInfo, [.. signatures], 450);
        ExtraFieldsV2 extraFieldsV2 = new ExtraFieldsV2(1, quorumCert);

        EncodeExtraData(extraFieldsV2);
        return this;
    }
    private void EncodeExtraData(ExtraFieldsV2 extraFieldsV2)
    {
        ExtraConsensusDataDecoder exctraEncoder = new ExtraConsensusDataDecoder();
        Rlp extraEncoded = exctraEncoder.Encode(extraFieldsV2);
        XdcTestObjectInternal.ExtraData = [0x2, .. extraEncoded.Bytes];
    }

    public XdcBlockHeaderBuilder WithExtraConsensusData(ExtraFieldsV2 extraFieldsV2)
    {
        XdcTestObjectInternal.ExtraConsensusData = extraFieldsV2;
        return this;
    }

    public new XdcBlockHeaderBuilder WithBaseFee(UInt256 baseFee)
    {
        TestObjectInternal.BaseFeePerGas = baseFee;
        return this;
    }

    public new XdcBlockHeaderBuilder WithHash(Hash256 hash256)
    {
        TestObjectInternal.Hash = hash256;
        return this;
    }

    public XdcBlockHeaderBuilder WithValidator(Signature signature)
    {
        XdcTestObjectInternal.Validator = signature.Bytes.ToArray();
        return this;
    }
    public XdcBlockHeaderBuilder WithValidator(byte[] bytes)
    {
        XdcTestObjectInternal.Validator = bytes;
        return this;
    }
    public XdcBlockHeaderBuilder WithValidators(byte[] validators)
    {
        XdcTestObjectInternal.Validators = validators;
        return this;
    }
    public XdcBlockHeaderBuilder WithPenalties(byte[] penalties)
    {
        XdcTestObjectInternal.Penalties = penalties;
        return this;
    }
}
