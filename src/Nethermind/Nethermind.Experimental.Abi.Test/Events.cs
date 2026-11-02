// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Experimental.Abi.Test;

[Parallelizable(ParallelScope.All)]
public class Events
{
    private static IEnumerable<TestCaseData> HashTestCases()
    {
        yield return Case(new AbiSignature("DepositEvent").With(AbiType.Bytes, AbiType.Bytes, AbiType.Bytes, AbiType.Bytes, AbiType.Bytes),
            "0x649bbc62d0e31342afea4e5cd82d4049e7e1ee912fc0889aa790803be39038c5");
        yield return Case(new AbiSignature("Transfer").With(AbiType.Address, AbiType.Address, AbiType.UInt256),
            "0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef");
        yield return Case(new AbiSignature("TransactionDeposited").With(AbiType.Address, AbiType.Address, AbiType.UInt256, AbiType.Bytes),
            "0xb3813568d9991fc951961fcb4c784893574240a28925604d09fc577c55bb7c32");
        yield return Case(new AbiSignature("ConfigUpdate").With(AbiType.UInt256, AbiType.UInt8, AbiType.Bytes),
            "0x1d2b0bda21d56b8bd12d4f94ebacffdfb35f5e226f84b461103bb8beab6353be");
    }

    private static TestCaseData Case<T>(AbiSignature<T> signature, string hash) =>
        new TestCaseData(signature, new Hash256(hash)).SetArgDisplayNames(signature.ToString());

    [TestCaseSource(nameof(HashTestCases))]
    public void Hash<T>(AbiSignature<T> signature, Hash256 hash) =>
        Assert.That(signature.Hash, Is.EqualTo(hash));

    [Test]
    public void TransferLog()
    {
        AbiSignature<(Address, Address, UInt256)> transfer = new AbiSignature("Transfer").With(AbiType.Address, AbiType.Address, AbiType.UInt256);
        AbiSignature<Address> address = new AbiSignature().With(AbiType.Address);
        Hash256[] topics =
        [
            new("0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef"),
            new("0x0000000000000000000000005a4eab120fb44eb6684e5e32785702ff45ea344d"), // `from`
            new("0x000000000000000000000000dac17f958d2ee523a2206206994597c13d831ec7"), // `to`
        ];
        byte[] data = Bytes.FromHexString("0000000000000000000000000000000000000000000000000de0b6b3a7640000"); // `value`, `10^18`

        using (Assert.EnterMultipleScope())
        {
            Assert.That(topics[0], Is.EqualTo(transfer.Hash));
            Assert.That(AbiCodec.Decode(address, topics[1].Bytes), Is.EqualTo(new Address("0x5a4eab120fb44eb6684e5e32785702ff45ea344d")));
            Assert.That(AbiCodec.Decode(address, topics[2].Bytes), Is.EqualTo(new Address("0xdac17f958d2ee523a2206206994597c13d831ec7")));
            Assert.That(AbiCodec.Decode(new AbiSignature().With(AbiType.UInt256), data), Is.EqualTo((UInt256)1_000_000_000_000_000_000));
        }
    }
}
