// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;

namespace Nethermind.Core.Test;

/// <summary>
/// Pins the EIP-8038 gas parameters to the spec's derivation formulas. The base values are
/// placeholders equal to the current (pre-8038) costs while the EIP is a Draft; these tests
/// guard the relationships so the derived values stay correct when the final figures land.
/// </summary>
public class Eip8038ConstantsTests
{
    [Test]
    public void Base_parameters_match_their_current_placeholder_values()
    {
        long coldAccountAccess = Eip8038Constants.ColdAccountAccess;
        long warmAccess = Eip8038Constants.WarmAccess;
        long coldStorageAccess = Eip8038Constants.ColdStorageAccess;
        long accountWrite = Eip8038Constants.AccountWrite;
        long storageWrite = Eip8038Constants.StorageWrite;
        long callStipend = Eip8038Constants.CallStipend;

        Assert.Multiple(() =>
        {
            Assert.That(coldAccountAccess, Is.EqualTo(2600));
            Assert.That(warmAccess, Is.EqualTo(100));
            Assert.That(coldStorageAccess, Is.EqualTo(2100));
            Assert.That(accountWrite, Is.EqualTo(6700));
            Assert.That(storageWrite, Is.EqualTo(2800));
            Assert.That(callStipend, Is.EqualTo(2300));
        });
    }

    [Test]
    public void Account_write_is_call_value_minus_stipend()
    {
        long accountWrite = Eip8038Constants.AccountWrite;
        Assert.That(accountWrite, Is.EqualTo(GasCostOf.CallValue - GasCostOf.CallStipend));
    }

    [Test]
    public void Call_value_is_account_write_plus_stipend()
    {
        long callValue = Eip8038Constants.CallValue;
        Assert.That(callValue, Is.EqualTo(Eip8038Constants.AccountWrite + Eip8038Constants.CallStipend));
    }

    [Test]
    public void Create_access_is_account_write_plus_cold_storage_access()
    {
        long createAccess = Eip8038Constants.CreateAccess;
        Assert.That(createAccess, Is.EqualTo(Eip8038Constants.AccountWrite + Eip8038Constants.ColdStorageAccess));
    }

    [Test]
    public void Access_list_address_cost_equals_cold_account_access()
    {
        long addressCost = Eip8038Constants.AccessListAddressCost;
        Assert.That(addressCost, Is.EqualTo(Eip8038Constants.ColdAccountAccess));
    }

    [Test]
    public void Access_list_storage_key_cost_equals_cold_storage_access()
    {
        long storageKeyCost = Eip8038Constants.AccessListStorageKeyCost;
        Assert.That(storageKeyCost, Is.EqualTo(Eip8038Constants.ColdStorageAccess));
    }

    [Test]
    public void Access_list_costs_are_raised_above_the_eip2930_values()
    {
        long addressCost = Eip8038Constants.AccessListAddressCost;
        long storageKeyCost = Eip8038Constants.AccessListStorageKeyCost;

        Assert.Multiple(() =>
        {
            Assert.That(addressCost, Is.GreaterThan(GasCostOf.AccessAccountListEntry));
            Assert.That(storageKeyCost, Is.GreaterThan(GasCostOf.AccessStorageListEntry));
        });
    }

    [Test]
    public void Storage_clear_refund_follows_the_derivation_formula()
    {
        long storageClearRefund = Eip8038Constants.StorageClearRefund;
        long expected = (Eip8038Constants.StorageWrite + Eip8038Constants.ColdStorageAccess) * 4800 / 5000;
        Assert.That(storageClearRefund, Is.EqualTo(expected));
    }
}
