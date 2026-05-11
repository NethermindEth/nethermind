// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
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
    public static void EqualToTransaction(this Transaction subject, Transaction expectation) =>
        Assert.Multiple(() =>
        {
            Assert.That(subject.ChainId, Is.EqualTo(expectation.ChainId));
            Assert.That(subject.Type, Is.EqualTo(expectation.Type));
            Assert.That(subject.IsAnchorTx, Is.EqualTo(expectation.IsAnchorTx));
            Assert.That(subject.SourceHash, Is.EqualTo(expectation.SourceHash));
            Assert.That(subject.Mint, Is.EqualTo(expectation.Mint));
            Assert.That(subject.IsOPSystemTransaction, Is.EqualTo(expectation.IsOPSystemTransaction));
            Assert.That(subject.Nonce, Is.EqualTo(expectation.Nonce));
            Assert.That(subject.GasPrice, Is.EqualTo(expectation.GasPrice));
            Assert.That(subject.GasBottleneck, Is.EqualTo(expectation.GasBottleneck));
            Assert.That(subject.DecodedMaxFeePerGas, Is.EqualTo(expectation.DecodedMaxFeePerGas));
            Assert.That(subject.GasLimit, Is.EqualTo(expectation.GasLimit));
            Assert.That(subject.SpentGas, Is.EqualTo(expectation.SpentGas));
            Assert.That(subject.BlockGasUsed, Is.EqualTo(expectation.BlockGasUsed));
            Assert.That(subject.To, Is.EqualTo(expectation.To));
            Assert.That(subject.Value, Is.EqualTo(expectation.Value));
            Assert.That(subject.Data.ToArray(), Is.EqualTo(expectation.Data.ToArray()));
            Assert.That(subject.SenderAddress, Is.EqualTo(expectation.SenderAddress));
            Assert.That(subject.Signature, Is.EqualTo(expectation.Signature));
            Assert.That(subject.Timestamp, Is.EqualTo(expectation.Timestamp));
            Assert.That(ToComparableAccessList(subject.AccessList), Is.EqualTo(ToComparableAccessList(expectation.AccessList)));
            Assert.That(subject.MaxFeePerBlobGas, Is.EqualTo(expectation.MaxFeePerBlobGas));
            Assert.That(subject.BlobVersionedHashes, Is.EqualTo(expectation.BlobVersionedHashes));
            AssertNetworkWrapper(subject.NetworkWrapper, expectation.NetworkWrapper);
            Assert.That(ToComparableAuthorizationList(subject.AuthorizationList), Is.EqualTo(ToComparableAuthorizationList(expectation.AuthorizationList)));
            Assert.That(subject.IsServiceTransaction, Is.EqualTo(expectation.IsServiceTransaction));
            Assert.That(subject.PoolIndex, Is.EqualTo(expectation.PoolIndex));
            Assert.That(subject.Hash, Is.EqualTo(expectation.Hash));
        });

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
