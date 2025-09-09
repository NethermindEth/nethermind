// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
using System;
using Nethermind.Xdc;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

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
