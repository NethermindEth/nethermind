// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;
using Nethermind.Core;
using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Shutter.Contracts;
using NSubstitute;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Shutter.Config;
using Nethermind.Crypto;
using Nethermind.Core.Crypto;

using Update = (byte[] Message, byte[] Signature);
using G1 = Nethermind.Crypto.Bls.P1;

namespace Nethermind.Shutter.Test;

[TestFixture]
class ShutterValidatorRegistryTests
{
    private static readonly byte[] SkBytes = [0x2c, 0xd4, 0xba, 0x40, 0x6b, 0x52, 0x24, 0x59, 0xd5, 0x7a, 0x0b, 0xed, 0x51, 0xa3, 0x97, 0x43, 0x5c, 0x0b, 0xb1, 0x1d, 0xd5, 0xf3, 0xca, 0x11, 0x52, 0xb3, 0x69, 0x4b, 0xb9, 0x1d, 0x7c, 0x22];

    [Test]
    public void Can_check_if_registered()
    {
        ValidatorRegistryContract contract = new(
            Substitute.For<ITransactionProcessor>(),
            ShutterTestsCommon.AbiEncoder,
            Address.Zero,
            LimboLogs.Instance,
            ShutterTestsCommon.ChainId,
            1);
        ShutterValidatorsInfo validatorsInfo = new();
        List<(uint, Update)> updates = [];

        // populate validatorsInfo
        G1 pk = new();
        for (ulong i = 100; i < 110; i++)
        {
            Bls.SecretKey sk = GetSecretKeyForIndex((uint)i);
            pk.FromSk(sk);
            validatorsInfo.Add(i, pk.ToAffine().Point.ToArray());
        }

        // register all 10, then deregister last 5
        updates.Add((0, CreateUpdate(100, 10, 0, 1, true)));
        updates.Add((1, CreateUpdate(105, 5, 1, 1, false)));

        // reregister 1 with V0 signature
        updates.Add((2, CreateUpdate(107, 1, 2, 0, true)));

        // invalid updates should be ignored
        updates.Add((3, CreateUpdate(100, 10, 0, 1, false))); // invalid nonce
        updates.Add((4, CreateUpdate(50, 50, 0, 1, true))); // not in validatorsInfo

        // bad signature
        Update badUpdate = CreateUpdate(100, 10, 2, 1, true);
        badUpdate.Signature[34] += 1;
        updates.Add((5, badUpdate));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(!contract.IsRegistered(updates, validatorsInfo, out HashSet<ulong> unregistered, CancellationToken.None));
            Assert.That(unregistered, Has.Count.EqualTo(4));
        }
    }

    private static Update CreateUpdate(ulong startIndex, uint count, uint nonce, byte version, bool isRegistration)
    {
        ValidatorRegistryContract.Message msg = new()
        {
            Version = version,
            ChainId = ShutterTestsCommon.ChainId,
            ContractAddress = Address.Zero.Bytes,
            StartValidatorIndex = startIndex,
            Count = count,
            Nonce = nonce,
            IsRegistration = isRegistration
        };
        byte[] msgBytes = msg.Encode();
        ReadOnlySpan<byte> msgHash = ValueKeccak.Compute(msgBytes).Bytes;

        BlsSigner.Signature agg = new();
        BlsSigner.Signature s = new();

        ulong endIndex = startIndex + count;
        for (ulong i = startIndex; i < endIndex; i++)
        {
            Bls.SecretKey sk = GetSecretKeyForIndex((uint)i);
            s.Sign(sk, msgHash);
            agg.Aggregate(s);
        }

        return (msgBytes, agg.Bytes.ToArray());
    }

    private static Bls.SecretKey GetSecretKeyForIndex(uint index)
    {
        // n.b. doesn't have to derive from master key, just done for convenience
        Bls.SecretKey masterSk = new(SkBytes, Bls.ByteOrder.LittleEndian);
        return new(masterSk, index);
    }
}
