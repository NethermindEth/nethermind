// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core.Collections;
using NUnit.Framework;

namespace Nethermind.Core.Test;

public static class BlockTestAssertions
{
    public static void AssertBlockEquivalent(Block? actual, Block? expected)
    {
        if (expected is null)
        {
            Assert.That(actual, Is.Null);
            return;
        }

        Assert.That(actual, Is.Not.Null);
        if (actual is null)
        {
            return;
        }

        Assert.Multiple(() =>
        {
            AssertBlockHeaderEquivalent(actual.Header, expected.Header);
            AssertBlockBodyEquivalent(actual.Body, expected.Body);
            Assert.That(actual.BlockAccessList, Is.EqualTo(expected.BlockAccessList));
            Assert.That(actual.GeneratedBlockAccessList, Is.EqualTo(expected.GeneratedBlockAccessList));
            AssertJaggedBytes(actual.ExecutionRequests, expected.ExecutionRequests);
            AssertAccountChangesEquivalent(actual.AccountChanges, expected.AccountChanges);
            Assert.That(actual.EncodedBlockAccessList, Is.EqualTo(expected.EncodedBlockAccessList));
            AssertJaggedBytes(actual.EncodedTransactions, expected.EncodedTransactions);
        });
    }

    public static void AssertBlockBodyEquivalent(BlockBody? actual, BlockBody? expected)
    {
        if (expected is null)
        {
            Assert.That(actual, Is.Null);
            return;
        }

        Assert.That(actual, Is.Not.Null);
        if (actual is null)
        {
            return;
        }

        Assert.Multiple(() =>
        {
            actual.Transactions.EqualToTransactions(expected.Transactions);
            Assert.That(actual.Uncles, Has.Length.EqualTo(expected.Uncles.Length));
            AssertWithdrawalsEquivalent(actual.Withdrawals, expected.Withdrawals);
        });

        for (int i = 0; i < expected.Uncles.Length; i++)
        {
            AssertBlockHeaderEquivalent(actual.Uncles[i], expected.Uncles[i]);
        }
    }

    public static void AssertBlockHeaderEquivalent(BlockHeader? actual, BlockHeader? expected)
    {
        if (expected is null)
        {
            Assert.That(actual, Is.Null);
            return;
        }

        Assert.That(actual, Is.Not.Null);
        if (actual is null)
        {
            return;
        }

        Assert.Multiple(() =>
        {
            Assert.That(actual.ParentHash, Is.EqualTo(expected.ParentHash));
            Assert.That(actual.UnclesHash, Is.EqualTo(expected.UnclesHash));
            Assert.That(actual.Author, Is.EqualTo(expected.Author));
            Assert.That(actual.Beneficiary, Is.EqualTo(expected.Beneficiary));
            Assert.That(actual.StateRoot, Is.EqualTo(expected.StateRoot));
            Assert.That(actual.TxRoot, Is.EqualTo(expected.TxRoot));
            Assert.That(actual.ReceiptsRoot, Is.EqualTo(expected.ReceiptsRoot));
            Assert.That(actual.Bloom, Is.EqualTo(expected.Bloom));
            Assert.That(actual.Difficulty, Is.EqualTo(expected.Difficulty));
            Assert.That(actual.Number, Is.EqualTo(expected.Number));
            Assert.That(actual.GasUsed, Is.EqualTo(expected.GasUsed));
            Assert.That(actual.GasLimit, Is.EqualTo(expected.GasLimit));
            Assert.That(actual.Timestamp, Is.EqualTo(expected.Timestamp));
            Assert.That(actual.ExtraData, Is.EqualTo(expected.ExtraData));
            Assert.That(actual.MixHash, Is.EqualTo(expected.MixHash));
            Assert.That(actual.Nonce, Is.EqualTo(expected.Nonce));
            Assert.That(actual.Hash, Is.EqualTo(expected.Hash));
            Assert.That(actual.TotalDifficulty, Is.EqualTo(expected.TotalDifficulty));
            Assert.That(actual.AuRaSignature, Is.EqualTo(expected.AuRaSignature));
            Assert.That(actual.AuRaStep, Is.EqualTo(expected.AuRaStep));
            Assert.That(actual.BaseFeePerGas, Is.EqualTo(expected.BaseFeePerGas));
            Assert.That(actual.WithdrawalsRoot, Is.EqualTo(expected.WithdrawalsRoot));
            Assert.That(actual.ParentBeaconBlockRoot, Is.EqualTo(expected.ParentBeaconBlockRoot));
            Assert.That(actual.RequestsHash, Is.EqualTo(expected.RequestsHash));
            Assert.That(actual.BlockAccessListHash, Is.EqualTo(expected.BlockAccessListHash));
            Assert.That(actual.BlobGasUsed, Is.EqualTo(expected.BlobGasUsed));
            Assert.That(actual.ExcessBlobGas, Is.EqualTo(expected.ExcessBlobGas));
            Assert.That(actual.SlotNumber, Is.EqualTo(expected.SlotNumber));
            Assert.That(actual.IsPostMerge, Is.EqualTo(expected.IsPostMerge));
        });
    }

    private static void AssertWithdrawalsEquivalent(Withdrawal[]? actual, Withdrawal[]? expected)
    {
        if (expected is null)
        {
            Assert.That(actual, Is.Null);
            return;
        }

        Assert.That(actual, Is.Not.Null);
        if (actual is null)
        {
            return;
        }

        Assert.That(actual, Has.Length.EqualTo(expected.Length));
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Multiple(() =>
            {
                Assert.That(actual[i].Index, Is.EqualTo(expected[i].Index));
                Assert.That(actual[i].ValidatorIndex, Is.EqualTo(expected[i].ValidatorIndex));
                Assert.That(actual[i].Address, Is.EqualTo(expected[i].Address));
                Assert.That(actual[i].AmountInGwei, Is.EqualTo(expected[i].AmountInGwei));
            });
        }
    }

    private static void AssertJaggedBytes(byte[][]? actual, byte[][]? expected)
    {
        if (expected is null)
        {
            Assert.That(actual, Is.Null);
            return;
        }

        Assert.That(actual, Is.Not.Null);
        if (actual is null)
        {
            return;
        }

        Assert.That(actual, Has.Length.EqualTo(expected.Length));
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.That(actual[i], Is.EqualTo(expected[i]));
        }
    }

    private static void AssertAccountChangesEquivalent(ArrayPoolList<AddressAsKey>? actual, ArrayPoolList<AddressAsKey>? expected)
    {
        if (expected is null)
        {
            Assert.That(actual, Is.Null);
            return;
        }

        Assert.That(actual, Is.Not.Null);
        if (actual is null)
        {
            return;
        }

        Assert.That(actual.ToArray(), Is.EqualTo(expected.ToArray()));
    }
}
