// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
using System;
using Nethermind.Xdc;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Core.Test.Builders;

public class XdcBlockHeaderBuilder : BuilderBase<XdcBlockHeader>
{
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
            Nonce = 1000,
            Validators = new byte[20 * 2],
            Validator = new byte[20],
            Penalties = Array.Empty<byte>(),
        };
    }

    public XdcBlockHeaderBuilder WithBaseFee(UInt256 baseFee)
    {
        TestObjectInternal.BaseFeePerGas = baseFee;
        return this;
    }
}
