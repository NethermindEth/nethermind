// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
using System;
using Nethermind.Xdc;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Xdc.Types;
using System.Linq;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Core.Test.Builders;

public class XdcBlockHeaderBuilder : BlockHeaderBuilder
{
    private XdcBlockHeader XdcTestObjectInternal => (XdcBlockHeader)TestObjectInternal;

    public new XdcBlockHeader TestObject => (XdcBlockHeader)TestObjectInternal;


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

    public XdcBlockHeaderBuilder WithGeneratedExtraConsensusData()
    {
        var encoder = new QuorumCertificateDecoder();
        var ecdsa = new EthereumEcdsa(0);
        var keyBuilder = new PrivateKeyGenerator();
        var blockRoundInfo = new BlockRoundInfo(Hash256.Zero, 1, 1);
        var quorumForSigning = new QuorumCert(blockRoundInfo, null, 450);
        var signatures = Enumerable.Range(0, 72).Select(i => keyBuilder.Generate()).Select(k => ecdsa.Sign(k, Keccak.Compute(encoder.Encode(quorumForSigning, RlpBehaviors.ForSealing).Bytes)));
        var quorumCert = new QuorumCert(blockRoundInfo, signatures.ToArray(), 450);
        var extraFieldsV2 = new ExtraFieldsV2(1, quorumCert);
        XdcTestObjectInternal.ExtraConsensusData = extraFieldsV2;
        return this;
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
