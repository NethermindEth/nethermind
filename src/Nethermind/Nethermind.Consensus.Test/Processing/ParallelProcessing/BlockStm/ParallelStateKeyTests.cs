// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Processing.ParallelProcessing.BlockStm;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Processing.ParallelProcessing.BlockStm;

public class ParallelStateKeyTests
{
    private static readonly Address AddrA = TestItem.AddressA;
    private static readonly Address AddrB = TestItem.AddressB;

    [Test]
    public void Account_and_storage_clear_distinct_from_storage_at_emptytreehash()
    {
        // Audit B2: the codex version aliased the storage-clear marker with a real
        // SSTORE at index Keccak.EmptyTreeHash. The fix moved Account and StorageClear
        // to dedicated kinds keyed by Address.
        ParallelStateKey account = ParallelStateKey.ForAccount(AddrA);
        ParallelStateKey clear = ParallelStateKey.ForStorageClear(AddrA);
        ParallelStateKey storage = ParallelStateKey.ForStorage(new StorageCell(AddrA, Keccak.EmptyTreeHash.ValueHash256));

        Assert.That(account, Is.Not.EqualTo(clear));
        Assert.That(account, Is.Not.EqualTo(storage));
        Assert.That(clear, Is.Not.EqualTo(storage));

        Assert.That(account.GetHashCode(), Is.Not.EqualTo(clear.GetHashCode()));
        Assert.That(clear.GetHashCode(), Is.Not.EqualTo(storage.GetHashCode()));
    }

    [Test]
    public void Account_keys_for_different_addresses_are_distinct() =>
        Assert.That(ParallelStateKey.ForAccount(AddrA), Is.Not.EqualTo(ParallelStateKey.ForAccount(AddrB)));

    [Test]
    public void Storage_clear_keys_for_different_addresses_are_distinct() =>
        Assert.That(ParallelStateKey.ForStorageClear(AddrA), Is.Not.EqualTo(ParallelStateKey.ForStorageClear(AddrB)));

    [Test]
    public void Fee_keys_distinguish_kind_and_index()
    {
        ParallelStateKey beneficiary0 = ParallelStateKey.ForFee(FeeRecipientKind.GasBeneficiary, 0);
        ParallelStateKey beneficiary1 = ParallelStateKey.ForFee(FeeRecipientKind.GasBeneficiary, 1);
        ParallelStateKey collector0 = ParallelStateKey.ForFee(FeeRecipientKind.FeeCollector, 0);

        Assert.That(beneficiary0, Is.Not.EqualTo(beneficiary1));
        Assert.That(beneficiary0, Is.Not.EqualTo(collector0));
        Assert.That(beneficiary0, Is.EqualTo(ParallelStateKey.ForFee(FeeRecipientKind.GasBeneficiary, 0)));
    }

    [Test]
    public void Storage_keys_with_same_cell_equal()
    {
        UInt256 idx = 42;
        Assert.That(
            ParallelStateKey.ForStorage(new StorageCell(AddrA, idx)),
            Is.EqualTo(ParallelStateKey.ForStorage(new StorageCell(AddrA, idx))));
    }

    [Test]
    public void Address_property_returns_correct_address_for_account_clear_and_storage()
    {
        Assert.That(ParallelStateKey.ForAccount(AddrA).Address, Is.EqualTo(AddrA));
        Assert.That(ParallelStateKey.ForStorageClear(AddrA).Address, Is.EqualTo(AddrA));
        Assert.That(ParallelStateKey.ForStorage(new StorageCell(AddrA, UInt256.Zero)).Address, Is.EqualTo(AddrA));
    }
}
