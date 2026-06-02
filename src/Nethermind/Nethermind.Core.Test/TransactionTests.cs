// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core.Eip2930;
using Nethermind.Int256;
using NUnit.Framework;
using NUnit.Framework.Constraints;

namespace Nethermind.Core.Test;

public class TransactionTests
{
    [Test]
    public void When_to_not_empty_then_is_message_call()
    {
        Transaction transaction = new();
        transaction.To = Address.Zero;
        Assert.That(transaction.IsMessageCall, Is.True, nameof(Transaction.IsMessageCall));
        Assert.That(transaction.IsContractCreation, Is.False, nameof(Transaction.IsContractCreation));
    }

    [Test]
    public void When_to_empty_then_is_message_call()
    {
        Transaction transaction = new();
        transaction.To = null;
        Assert.That(transaction.IsMessageCall, Is.False, nameof(Transaction.IsMessageCall));
        Assert.That(transaction.IsContractCreation, Is.True, nameof(Transaction.IsContractCreation));
    }

    [TestCase(1, true)]
    [TestCase(300, true)]
    public void Supports1559_returns_expected_results(int decodedFeeCap, bool expectedSupports1559)
    {
        Transaction transaction = new();
        transaction.DecodedMaxFeePerGas = (uint)decodedFeeCap;
        transaction.Type = TxType.EIP1559;
        Assert.That(transaction.DecodedMaxFeePerGas, Is.EqualTo(transaction.MaxFeePerGas));
        Assert.That(transaction.Supports1559, Is.EqualTo(expectedSupports1559));
    }
}

public static class TransactionTestExtensions
{
    public static EqualConstraint UsingTransactionComparer(this EqualConstraint constraint, params string[] excludedProperties)
    {
        string[] excluded =
        [
            nameof(Transaction.MaxPriorityFeePerGas),
            nameof(Transaction.ValueRef),
            .. excludedProperties
        ];

        return constraint
            .Using<ReadOnlyMemory<byte>>(static (actual, expected) => actual.Span.SequenceEqual(expected.Span))
            .Using<AccessList>(AccessListsEqual)
            .Using<AuthorizationTuple[]>(AuthorizationListsEqual)
            .Using<ShardBlobNetworkWrapper>(ShardBlobNetworkWrappersEqual)
            .UsingPropertiesComparer(options => options.Excluding(excluded));
    }

    private static bool AccessListsEqual(AccessList? actual, AccessList? expected)
    {
        if (actual is null || expected is null)
        {
            return actual is null && expected is null;
        }

        (Address Address, UInt256[] StorageKeys)[] actualEntries = actual
            .Select(static entry => (entry.Address, entry.StorageKeys.ToArray()))
            .ToArray();
        (Address Address, UInt256[] StorageKeys)[] expectedEntries = expected
            .Select(static entry => (entry.Address, entry.StorageKeys.ToArray()))
            .ToArray();

        if (actualEntries.Length != expectedEntries.Length)
        {
            return false;
        }

        for (int i = 0; i < expectedEntries.Length; i++)
        {
            if (actualEntries[i].Address != expectedEntries[i].Address ||
                !actualEntries[i].StorageKeys.SequenceEqual(expectedEntries[i].StorageKeys))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AuthorizationListsEqual(AuthorizationTuple[]? actual, AuthorizationTuple[]? expected)
    {
        if (actual is null || expected is null)
        {
            return actual is null && expected is null;
        }

        if (actual.Length != expected.Length)
        {
            return false;
        }

        for (int i = 0; i < expected.Length; i++)
        {
            if (actual[i].ChainId != expected[i].ChainId ||
                actual[i].CodeAddress != expected[i].CodeAddress ||
                actual[i].Nonce != expected[i].Nonce ||
                actual[i].AuthoritySignature != expected[i].AuthoritySignature ||
                actual[i].Authority != expected[i].Authority)
            {
                return false;
            }
        }

        return true;
    }

    private static bool ShardBlobNetworkWrappersEqual(ShardBlobNetworkWrapper? actual, ShardBlobNetworkWrapper? expected)
    {
        if (actual is null || expected is null)
        {
            return actual is null && expected is null;
        }

        return actual.Version == expected.Version &&
            JaggedBytesEqual(actual.Blobs, expected.Blobs) &&
            JaggedBytesEqual(actual.Commitments, expected.Commitments) &&
            JaggedBytesEqual(actual.Proofs, expected.Proofs);
    }

    private static bool JaggedBytesEqual(byte[][]? actual, byte[][]? expected)
    {
        if (actual is null || expected is null)
        {
            return actual is null && expected is null;
        }

        if (actual.Length != expected.Length)
        {
            return false;
        }

        for (int i = 0; i < expected.Length; i++)
        {
            if (!actual[i].SequenceEqual(expected[i]))
            {
                return false;
            }
        }

        return true;
    }
}
