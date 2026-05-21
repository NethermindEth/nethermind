// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Test;
using NUnit.Framework;
using NUnit.Framework.Constraints;

namespace Nethermind.Xdc.Test;

internal static class XdcTestAssertions
{
    public static void AssertXdcHeader(XdcBlockHeader? actual, XdcBlockHeader expected, bool compareHash = true)
    {
        Assert.That(actual, Is.Not.Null);
        if (actual is null)
        {
            return;
        }

        Assert.Multiple(() =>
        {
            if (compareHash)
            {
                Assert.That(actual.Hash, Is.EqualTo(expected.Hash));
            }

            Assert.That(actual.ParentHash, Is.EqualTo(expected.ParentHash));
            Assert.That(actual.UnclesHash, Is.EqualTo(expected.UnclesHash));
            Assert.That(actual.Beneficiary, Is.EqualTo(expected.Beneficiary));
            Assert.That(actual.Difficulty, Is.EqualTo(expected.Difficulty));
            Assert.That(actual.Number, Is.EqualTo(expected.Number));
            Assert.That(actual.GasLimit, Is.EqualTo(expected.GasLimit));
            Assert.That(actual.GasUsed, Is.EqualTo(expected.GasUsed));
            Assert.That(actual.Timestamp, Is.EqualTo(expected.Timestamp));
            Assert.That(actual.ExtraData, Is.EqualTo(expected.ExtraData));
            Assert.That(actual.Validators, Is.EqualTo(expected.Validators));
            Assert.That(actual.Validator, Is.EqualTo(expected.Validator));
            Assert.That(actual.Penalties, Is.EqualTo(expected.Penalties));
            Assert.That(actual.TxRoot, Is.EqualTo(expected.TxRoot));
            Assert.That(actual.StateRoot, Is.EqualTo(expected.StateRoot));
            Assert.That(actual.ReceiptsRoot, Is.EqualTo(expected.ReceiptsRoot));
            Assert.That(actual.Bloom, Is.EqualTo(expected.Bloom));
            Assert.That(actual.MixHash, Is.EqualTo(expected.MixHash));
            Assert.That(actual.Nonce, Is.EqualTo(expected.Nonce));
        });
    }

    public static void AssertBlock(Block? actual, Block expected)
    {
        Assert.That(actual, Is.Not.Null);
        if (actual is null)
        {
            return;
        }

        Assert.Multiple(() =>
        {
            AssertXdcHeader((XdcBlockHeader)actual.Header, (XdcBlockHeader)expected.Header);
            actual.Transactions.EqualToTransactions(expected.Transactions);
            Assert.That(actual.Uncles, Has.Length.EqualTo(expected.Uncles.Length));
        });

        for (int i = 0; i < expected.Uncles.Length; i++)
        {
            AssertXdcHeader((XdcBlockHeader)actual.Uncles[i], (XdcBlockHeader)expected.Uncles[i]);
        }
    }

    public static EqualConstraint UsingXdcProperties(this EqualConstraint constraint, params string[] excludedProperties)
    {
        constraint = constraint.Using<Address[]>(AddressArraysEquivalent);
        return excludedProperties.Length == 0
            ? constraint.UsingPropertiesComparer()
            : constraint.UsingPropertiesComparer(options => options.Excluding(excludedProperties));
    }

    private static bool AddressArraysEquivalent(Address[]? actual, Address[]? expected)
    {
        if (actual is null || expected is null)
        {
            return actual is null && expected is null;
        }

        if (actual.Length != expected.Length)
        {
            return false;
        }

        List<Address> unmatched = [.. actual];
        foreach (Address expectedAddress in expected)
        {
            int index = unmatched.IndexOf(expectedAddress);
            if (index < 0)
            {
                return false;
            }

            unmatched.RemoveAt(index);
        }

        return true;
    }
}
