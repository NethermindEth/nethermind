// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// Tests for EIP-8131: unified transaction content floor — one per-byte floor rule across
/// calldata, access lists, authorization tuples, and blob versioned hashes, replacing the
/// EIP-7623/EIP-7976/EIP-7981 floor rules.
/// </summary>
[TestFixture]
public class Eip8131Tests
{
    private static readonly IReleaseSpec Spec = new OverridableReleaseSpec(Amsterdam.Instance) { IsEip8131Enabled = true };

    private static ulong Floor(ulong contentBytes) => GasCostOf.Transaction + contentBytes * GasCostOf.FloorGasPerByteEip8131;

    [TestCase(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, TestName = "10 zero bytes")]
    [TestCase(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, TestName = "10 non-zero bytes")]
    public void Calldata_floor_is_flat_per_byte(byte[] data)
    {
        Transaction transaction = new() { To = Address.Zero, Data = data };
        EthereumIntrinsicGas cost = IntrinsicGasCalculator.Calculate(transaction, Spec);

        // Zero and non-zero bytes price identically under the unified floor.
        Assert.That(cost.FloorGas, Is.EqualTo(Floor((ulong)data.Length)));
    }

    [Test]
    public void Access_list_floor_uses_byte_sizes_and_standard_cost_drops_eip7981_surcharge()
    {
        AccessList accessList = new AccessList.Builder()
            .AddAddress(Address.Zero)
            .AddStorage(UInt256.Zero)
            .AddStorage(UInt256.One)
            .Build();
        Transaction transaction = new() { To = Address.Zero, AccessList = accessList };
        EthereumIntrinsicGas cost = IntrinsicGasCalculator.Calculate(transaction, Spec);

        using (Assert.EnterMultipleScope())
        {
            // 20 address bytes + 2 × 32 storage key bytes = 84 content bytes.
            Assert.That(cost.FloorGas, Is.EqualTo(Floor(84)));
            // EIP-8131 replaces EIP-7981, so the standard cost reverts to plain EIP-2930 pricing.
            Assert.That(cost.Standard, Is.EqualTo(
                GasCostOf.Transaction + GasCostOf.AccessAccountListEntry + 2 * GasCostOf.AccessStorageListEntry));
        }
    }

    [Test]
    public void Authorization_tuples_are_floor_priced_at_108_bytes_each()
    {
        AuthorizationTuple[] authList =
        [
            new AuthorizationTuple(1, TestItem.AddressA, 0, 0, UInt256.One, UInt256.One),
            new AuthorizationTuple(1, TestItem.AddressB, 1, 1, UInt256.One, UInt256.One),
        ];
        Transaction transaction = Build.A.Transaction.WithAuthorizationCode(authList).TestObject;
        EthereumIntrinsicGas cost = IntrinsicGasCalculator.Calculate(transaction, Spec);

        Assert.That(cost.FloorGas, Is.EqualTo(Floor(2 * GasCostOf.AuthTupleBytesEip8131)));
    }

    [Test]
    public void Blob_versioned_hashes_are_floor_priced_at_32_bytes_each()
    {
        Transaction transaction = new()
        {
            To = Address.Zero,
            Type = TxType.Blob,
            BlobVersionedHashes = [new byte[32], new byte[32], new byte[32]],
        };
        EthereumIntrinsicGas cost = IntrinsicGasCalculator.Calculate(transaction, Spec);

        Assert.That(cost.FloorGas, Is.EqualTo(Floor(3 * GasCostOf.BlobVersionedHashBytesEip8131)));
    }

    [Test]
    public void Combined_content_sums_all_components()
    {
        AccessList accessList = new AccessList.Builder().AddAddress(Address.Zero).Build();
        Transaction transaction = new()
        {
            To = Address.Zero,
            Data = new byte[5],
            AccessList = accessList,
            Type = TxType.Blob,
            BlobVersionedHashes = [new byte[32]],
        };
        EthereumIntrinsicGas cost = IntrinsicGasCalculator.Calculate(transaction, Spec);

        // 5 calldata + 20 address + 32 blob hash = 57 content bytes.
        Assert.That(cost.FloorGas, Is.EqualTo(Floor(57)));
    }

    [Test]
    public void Before_eip8131_blob_hashes_and_auth_tuples_are_not_floor_priced()
    {
        Transaction transaction = new()
        {
            To = Address.Zero,
            Type = TxType.Blob,
            BlobVersionedHashes = [new byte[32]],
        };
        EthereumIntrinsicGas cost = IntrinsicGasCalculator.Calculate(transaction, Amsterdam.Instance);

        Assert.That(cost.FloorGas, Is.EqualTo(GasCostOf.Transaction), "pre-8131 floor must ignore blob hashes");
    }
}
