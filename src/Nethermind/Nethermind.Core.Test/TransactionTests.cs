// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Int256;
using NUnit.Framework;

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
    public static void EqualToTransactions(this IEnumerable<Transaction> subjects, IEnumerable<Transaction> expectations, params string[] excludedProperties)
    {
        Transaction[] actual = subjects.ToArray();
        Transaction[] expected = expectations.ToArray();
        Assert.That(actual, Has.Length.EqualTo(expected.Length));
        for (int i = 0; i < expected.Length; i++)
        {
            actual[i].EqualToTransaction(expected[i], excludedProperties);
        }
    }

    public static void EqualToTransaction(this Transaction subject, Transaction expectation, params string[] excludedProperties) =>
        Assert.Multiple(() =>
        {
            if (!IsExcluded(excludedProperties, nameof(Transaction.ChainId)))
            {
                Assert.That(subject.ChainId, Is.EqualTo(expectation.ChainId));
            }

            if (!IsExcluded(excludedProperties, nameof(Transaction.Type)))
            {
                Assert.That(subject.Type, Is.EqualTo(expectation.Type));
            }

            if (!IsExcluded(excludedProperties, nameof(Transaction.IsAnchorTx)))
            {
                Assert.That(subject.IsAnchorTx, Is.EqualTo(expectation.IsAnchorTx));
            }

            if (!IsExcluded(excludedProperties, nameof(Transaction.SourceHash)))
            {
                Assert.That(subject.SourceHash, Is.EqualTo(expectation.SourceHash));
            }

            if (!IsExcluded(excludedProperties, nameof(Transaction.Mint)))
            {
                Assert.That(subject.Mint, Is.EqualTo(expectation.Mint));
            }

            if (!IsExcluded(excludedProperties, nameof(Transaction.IsOPSystemTransaction)))
            {
                Assert.That(subject.IsOPSystemTransaction, Is.EqualTo(expectation.IsOPSystemTransaction));
            }

            if (!IsExcluded(excludedProperties, nameof(Transaction.Nonce)))
            {
                Assert.That(subject.Nonce, Is.EqualTo(expectation.Nonce));
            }

            if (!IsExcluded(excludedProperties, nameof(Transaction.GasPrice)))
            {
                Assert.That(subject.GasPrice, Is.EqualTo(expectation.GasPrice));
            }

            if (!IsExcluded(excludedProperties, nameof(Transaction.GasBottleneck)))
            {
                Assert.That(subject.GasBottleneck, Is.EqualTo(expectation.GasBottleneck));
            }

            if (!IsExcluded(excludedProperties, nameof(Transaction.DecodedMaxFeePerGas)))
            {
                Assert.That(subject.DecodedMaxFeePerGas, Is.EqualTo(expectation.DecodedMaxFeePerGas));
            }

            if (!IsExcluded(excludedProperties, nameof(Transaction.GasLimit)))
            {
                Assert.That(subject.GasLimit, Is.EqualTo(expectation.GasLimit));
            }

            if (!IsExcluded(excludedProperties, nameof(Transaction.SpentGas)))
            {
                Assert.That(subject.SpentGas, Is.EqualTo(expectation.SpentGas));
            }

            if (!IsExcluded(excludedProperties, nameof(Transaction.BlockGasUsed)))
            {
                Assert.That(subject.BlockGasUsed, Is.EqualTo(expectation.BlockGasUsed));
            }

            if (!IsExcluded(excludedProperties, nameof(Transaction.To)))
            {
                Assert.That(subject.To, Is.EqualTo(expectation.To));
            }

            if (!IsExcluded(excludedProperties, nameof(Transaction.Value)))
            {
                Assert.That(subject.Value, Is.EqualTo(expectation.Value));
            }

            if (!IsExcluded(excludedProperties, nameof(Transaction.Data)))
            {
                Assert.That(subject.Data.ToArray(), Is.EqualTo(expectation.Data.ToArray()));
            }

            if (!IsExcluded(excludedProperties, nameof(Transaction.SenderAddress)))
            {
                Assert.That(subject.SenderAddress, Is.EqualTo(expectation.SenderAddress));
            }

            if (!IsExcluded(excludedProperties, nameof(Transaction.Signature)))
            {
                Assert.That(subject.Signature, Is.EqualTo(expectation.Signature));
            }

            if (!IsExcluded(excludedProperties, nameof(Transaction.Timestamp)))
            {
                Assert.That(subject.Timestamp, Is.EqualTo(expectation.Timestamp));
            }

            if (!IsExcluded(excludedProperties, nameof(Transaction.AccessList)))
            {
                Assert.That(ToComparableAccessList(subject.AccessList), Is.EqualTo(ToComparableAccessList(expectation.AccessList)));
            }

            if (!IsExcluded(excludedProperties, nameof(Transaction.MaxFeePerBlobGas)))
            {
                Assert.That(subject.MaxFeePerBlobGas, Is.EqualTo(expectation.MaxFeePerBlobGas));
            }

            if (!IsExcluded(excludedProperties, nameof(Transaction.BlobVersionedHashes)))
            {
                Assert.That(subject.BlobVersionedHashes, Is.EqualTo(expectation.BlobVersionedHashes));
            }

            if (!IsExcluded(excludedProperties, nameof(Transaction.NetworkWrapper)))
            {
                AssertNetworkWrapper(subject.NetworkWrapper, expectation.NetworkWrapper);
            }

            if (!IsExcluded(excludedProperties, nameof(Transaction.AuthorizationList)))
            {
                Assert.That(ToComparableAuthorizationList(subject.AuthorizationList), Is.EqualTo(ToComparableAuthorizationList(expectation.AuthorizationList)));
            }

            if (!IsExcluded(excludedProperties, nameof(Transaction.IsServiceTransaction)))
            {
                Assert.That(subject.IsServiceTransaction, Is.EqualTo(expectation.IsServiceTransaction));
            }

            if (!IsExcluded(excludedProperties, nameof(Transaction.PoolIndex)))
            {
                Assert.That(subject.PoolIndex, Is.EqualTo(expectation.PoolIndex));
            }

            if (!IsExcluded(excludedProperties, nameof(Transaction.Hash)))
            {
                Assert.That(subject.Hash, Is.EqualTo(expectation.Hash));
            }
        });

    private static bool IsExcluded(string[] excludedProperties, string propertyName) =>
        Array.IndexOf(excludedProperties, propertyName) >= 0;

    private static ComparableAccessListEntry[] ToComparableAccessList(AccessList? accessList) =>
        accessList?.Select(static entry => new ComparableAccessListEntry(
            entry.Address,
            string.Join(",", entry.StorageKeys.Select(static key => key.ToString())))).ToArray() ?? [];

    private static ComparableAuthorizationTuple[] ToComparableAuthorizationList(AuthorizationTuple[]? authorizationList) =>
        authorizationList?.Select(static tuple => new ComparableAuthorizationTuple(
            tuple.ChainId,
            tuple.CodeAddress,
            tuple.Nonce,
            tuple.AuthoritySignature,
            tuple.Authority)).ToArray() ?? [];

    private static void AssertNetworkWrapper(object? subject, object? expectation)
    {
        if (subject is ShardBlobNetworkWrapper actualWrapper && expectation is ShardBlobNetworkWrapper expectedWrapper)
        {
            Assert.Multiple(() =>
            {
                Assert.That(actualWrapper.Blobs, Is.EqualTo(expectedWrapper.Blobs));
                Assert.That(actualWrapper.Commitments, Is.EqualTo(expectedWrapper.Commitments));
                Assert.That(actualWrapper.Proofs, Is.EqualTo(expectedWrapper.Proofs));
                Assert.That(actualWrapper.Version, Is.EqualTo(expectedWrapper.Version));
            });
            return;
        }

        Assert.That(subject, Is.EqualTo(expectation));
    }

    private sealed record ComparableAccessListEntry(Address Address, string StorageKeys);

    private sealed record ComparableAuthorizationTuple(
        UInt256 ChainId,
        Address CodeAddress,
        ulong Nonce,
        Signature AuthoritySignature,
        Address? Authority);
}
