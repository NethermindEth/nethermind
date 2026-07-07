// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;

namespace Nethermind.Core.Test;

/// <summary>
/// Pins the EIP-8038 gas parameters and guards the derivation relationships so the derived
/// values stay consistent with the base parameters.
/// </summary>
public class Eip8038ConstantsTests
{
    [Test]
    public void Base_parameters_match_the_eip8038_values()
    {
        ulong coldAccountAccess = Eip8038Constants.ColdAccountAccess;
        ulong warmAccess = Eip8038Constants.WarmAccess;
        ulong coldStorageAccess = Eip8038Constants.ColdStorageAccess;
        ulong accountWrite = Eip8038Constants.AccountWrite;
        ulong storageWrite = Eip8038Constants.StorageWrite;
        ulong callStipend = Eip8038Constants.CallStipend;

        Assert.Multiple(() =>
        {
            Assert.That(coldAccountAccess, Is.EqualTo(3000));
            Assert.That(warmAccess, Is.EqualTo(100));
            Assert.That(coldStorageAccess, Is.EqualTo(3000));
            Assert.That(accountWrite, Is.EqualTo(8000));
            Assert.That(storageWrite, Is.EqualTo(10000));
            Assert.That(callStipend, Is.EqualTo(2300));
        });
    }

    [Test]
    public void Derived_parameters_match_the_eip8038_derivations() =>
        Assert.Multiple(() =>
        {
            Assert.That(Eip8038Constants.CallValue, Is.EqualTo(10300));
            Assert.That(Eip8038Constants.CreateAccess, Is.EqualTo(11000));
            Assert.That(Eip8038Constants.StorageClearRefund, Is.EqualTo(12480));
            Assert.That(Eip8038Constants.PerAuthBaseRegular, Is.EqualTo(15816));
        });

    [Test]
    public void Call_value_is_account_write_plus_stipend()
    {
        ulong callValue = Eip8038Constants.CallValue;
        Assert.That(callValue, Is.EqualTo(Eip8038Constants.AccountWrite + Eip8038Constants.CallStipend));
    }

    [Test]
    public void Create_access_is_account_write_plus_cold_storage_access()
    {
        ulong createAccess = Eip8038Constants.CreateAccess;
        Assert.That(createAccess, Is.EqualTo(Eip8038Constants.AccountWrite + Eip8038Constants.ColdStorageAccess));
    }

    [Test]
    public void Access_list_address_cost_equals_cold_account_access()
    {
        ulong addressCost = Eip8038Constants.AccessListAddressCost;
        Assert.That(addressCost, Is.EqualTo(Eip8038Constants.ColdAccountAccess));
    }

    [Test]
    public void Access_list_storage_key_cost_equals_cold_storage_access()
    {
        ulong storageKeyCost = Eip8038Constants.AccessListStorageKeyCost;
        Assert.That(storageKeyCost, Is.EqualTo(Eip8038Constants.ColdStorageAccess));
    }

    [Test]
    public void Access_list_costs_are_raised_above_the_eip2930_values()
    {
        ulong addressCost = Eip8038Constants.AccessListAddressCost;
        ulong storageKeyCost = Eip8038Constants.AccessListStorageKeyCost;

        Assert.Multiple(() =>
        {
            Assert.That(addressCost, Is.GreaterThan(GasCostOf.AccessAccountListEntry));
            Assert.That(storageKeyCost, Is.GreaterThan(GasCostOf.AccessStorageListEntry));
        });
    }

    [Test]
    public void Storage_clear_refund_follows_the_derivation_formula()
    {
        ulong storageClearRefund = Eip8038Constants.StorageClearRefund;
        ulong expected = (Eip8038Constants.StorageWrite + Eip8038Constants.ColdStorageAccess) * 4800 / 5000;
        Assert.That(storageClearRefund, Is.EqualTo(expected));
    }
}
