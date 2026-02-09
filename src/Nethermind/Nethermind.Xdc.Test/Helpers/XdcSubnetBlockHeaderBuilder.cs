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

namespace Nethermind.Core.Test.Builders;

public class XdcSubnetBlockHeaderBuilder : XdcBlockHeaderBuilder
{
    private XdcSubnetBlockHeader XdcTestObjectInternal => (XdcSubnetBlockHeader)TestObjectInternal;

    public new XdcSubnetBlockHeader TestObject => (XdcSubnetBlockHeader)base.TestObject;


    public XdcSubnetBlockHeaderBuilder()
    {
        TestObjectInternal = new XdcSubnetBlockHeader(
            Keccak.Compute("parent"),
            Keccak.OfAnEmptySequenceRlp,
            Address.Zero,
            UInt256.One,
            1,
            XdcConstants.TargetGasLimit,
            1_700_000_000,
            [])
        {
            StateRoot = Keccak.EmptyTreeHash,
            TxRoot = Keccak.EmptyTreeHash,
            ReceiptsRoot = Keccak.EmptyTreeHash,
            Bloom = Bloom.Empty,
            GasUsed = Transaction.BaseTxGasCost,
            MixHash = Keccak.Compute("mix_hash"),
            Nonce = 0,
            Validator = new byte[65],
            Validators = new byte[20 * 2],
            NextValidators = new byte[20 * 2],
            Penalties = Array.Empty<byte>(),
        };
    }
    public XdcSubnetBlockHeaderBuilder WithNextValidators(byte[] nextValidators)
    {
        XdcTestObjectInternal.NextValidators = nextValidators;
        return this;
    }
    public XdcSubnetBlockHeaderBuilder WithNextValidators(Address[] nextValidators)
    {
        XdcTestObjectInternal.NextValidators = nextValidators.SelectMany(a => a.Bytes).ToArray();
        return this;
    }
}
