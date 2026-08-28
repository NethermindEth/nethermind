// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Eip2930;
using Nethermind.Int256;
using NUnit.Framework.Constraints;

namespace Nethermind.Core.Test;

public static class TestEqualityConstraintExtensions
{
    public static EqualConstraint UsingTransactionComparer(this EqualConstraint constraint, params string[] excludedProperties) =>
        constraint.Using(TestEqualityComparers.ForTransaction(excludedProperties));

    public static CollectionItemsEqualConstraint UsingTransactionComparer(this CollectionItemsEqualConstraint constraint, params string[] excludedProperties) =>
        constraint.Using(TestEqualityComparers.ForTransaction(excludedProperties));

    public static EqualConstraint UsingAuthorizationTupleComparer(this EqualConstraint constraint) =>
        constraint.Using(TestEqualityComparers.AuthorizationTuple);

    public static EqualConstraint UsingWithdrawalComparer(this EqualConstraint constraint) =>
        constraint.Using(TestEqualityComparers.Withdrawal);

    public static EqualConstraint UsingBlockHeaderComparer(this EqualConstraint constraint, bool compareHash = true) =>
        constraint.Using(TestEqualityComparers.BlockHeader(compareHash));

    public static CollectionItemsEqualConstraint UsingBlockHeaderComparer(this CollectionItemsEqualConstraint constraint, bool compareHash = true) =>
        constraint.Using(TestEqualityComparers.BlockHeader(compareHash));

    public static EqualConstraint UsingBlockBodyComparer(this EqualConstraint constraint, bool compareHash = true) =>
        constraint.Using(TestEqualityComparers.BlockBody(compareHash));

    public static EqualConstraint UsingBlockComparer(this EqualConstraint constraint, bool compareHash = true) =>
        constraint.Using(TestEqualityComparers.Block(compareHash));

    public static CollectionItemsEqualConstraint UsingBlockComparer(this CollectionItemsEqualConstraint constraint, bool compareHash = true) =>
        constraint.Using(TestEqualityComparers.Block(compareHash));
}

public static class TestEqualityComparers
{
    public static IEqualityComparer<AuthorizationTuple> AuthorizationTuple { get; } = new AuthorizationTupleEqualityComparer();
    public static IEqualityComparer<Withdrawal> Withdrawal { get; } = new WithdrawalEqualityComparer();

    public static IEqualityComparer<Block> Block(bool compareHash = true) =>
        new BlockEqualityComparer(compareHash);

    public static IEqualityComparer<BlockBody> BlockBody(bool compareHash = true) =>
        new BlockBodyEqualityComparer(compareHash);

    public static IEqualityComparer<BlockHeader> BlockHeader(bool compareHash = true) =>
        new BlockHeaderEqualityComparer(compareHash);

    public static IEqualityComparer<Transaction> ForTransaction(params string[] excludedProperties) =>
        new TransactionEqualityComparer(excludedProperties);

    public static bool ArraysEqual<T>(T[]? actual, T[]? expected, IEqualityComparer<T>? comparer = null)
    {
        if (actual is null || expected is null)
        {
            return actual is null && expected is null;
        }

        if (actual.Length != expected.Length)
        {
            return false;
        }

        comparer ??= EqualityComparer<T>.Default;
        for (int i = 0; i < expected.Length; i++)
        {
            if (!comparer.Equals(actual[i], expected[i]))
            {
                return false;
            }
        }

        return true;
    }

    public static bool BytesEqual(byte[]? actual, byte[]? expected)
    {
        if (actual is null || expected is null)
        {
            return actual is null && expected is null;
        }

        return actual.AsSpan().SequenceEqual(expected);
    }

    public static bool ByteArraysEqual(byte[]?[]? actual, byte[]?[]? expected)
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
            if (!BytesEqual(actual[i], expected[i]))
            {
                return false;
            }
        }

        return true;
    }

    private sealed class TransactionEqualityComparer(string[] excludedProperties) : IEqualityComparer<Transaction>
    {
        private readonly HashSet<string> _excludedProperties =
        [
            nameof(Transaction.MaxPriorityFeePerGas),
            nameof(Transaction.ValueRef),
            .. excludedProperties
        ];

        public bool Equals(Transaction? actual, Transaction? expected)
        {
            if (actual is null || expected is null)
            {
                return actual is null && expected is null;
            }

            return EqualsIfIncluded(nameof(Transaction.ChainId), actual.ChainId, expected.ChainId) &&
                EqualsIfIncluded(nameof(Transaction.Type), actual.Type, expected.Type) &&
                EqualsIfIncluded(nameof(Transaction.IsAnchorTx), actual.IsAnchorTx, expected.IsAnchorTx) &&
                EqualsIfIncluded(nameof(Transaction.SourceHash), actual.SourceHash, expected.SourceHash) &&
                EqualsIfIncluded(nameof(Transaction.Mint), actual.Mint, expected.Mint) &&
                EqualsIfIncluded(nameof(Transaction.IsOPSystemTransaction), actual.IsOPSystemTransaction, expected.IsOPSystemTransaction) &&
                EqualsIfIncluded(nameof(Transaction.Nonce), actual.Nonce, expected.Nonce) &&
                EqualsIfIncluded(nameof(Transaction.GasPrice), actual.GasPrice, expected.GasPrice) &&
                EqualsIfIncluded(nameof(Transaction.GasBottleneck), actual.GasBottleneck, expected.GasBottleneck) &&
                EqualsIfIncluded(nameof(Transaction.DecodedMaxFeePerGas), actual.DecodedMaxFeePerGas, expected.DecodedMaxFeePerGas) &&
                EqualsIfIncluded(nameof(Transaction.GasLimit), actual.GasLimit, expected.GasLimit) &&
                EqualsIfIncluded(nameof(Transaction.SpentGas), actual.SpentGas, expected.SpentGas) &&
                EqualsIfIncluded(nameof(Transaction.BlockGasUsed), actual.BlockGasUsed, expected.BlockGasUsed) &&
                EqualsIfIncluded(nameof(Transaction.To), actual.To, expected.To) &&
                EqualsIfIncluded(nameof(Transaction.Value), actual.Value, expected.Value) &&
                EqualsIfIncluded(nameof(Transaction.Data), actual.Data.Span, expected.Data.Span) &&
                EqualsIfIncluded(nameof(Transaction.SenderAddress), actual.SenderAddress, expected.SenderAddress) &&
                EqualsIfIncluded(nameof(Transaction.Signature), actual.Signature, expected.Signature) &&
                EqualsIfIncluded(nameof(Transaction.Hash), actual.Hash, expected.Hash) &&
                EqualsIfIncluded(nameof(Transaction.Timestamp), actual.Timestamp, expected.Timestamp) &&
                EqualsIfIncluded(nameof(Transaction.AccessList), actual.AccessList, expected.AccessList, AccessListEqualityComparer.Instance) &&
                EqualsIfIncluded(nameof(Transaction.MaxFeePerBlobGas), actual.MaxFeePerBlobGas, expected.MaxFeePerBlobGas) &&
                ByteArraysEqualIfIncluded(nameof(Transaction.BlobVersionedHashes), actual.BlobVersionedHashes, expected.BlobVersionedHashes) &&
                NetworkWrappersEqualIfIncluded(actual.NetworkWrapper, expected.NetworkWrapper) &&
                ArraysEqualIfIncluded(nameof(Transaction.AuthorizationList), actual.AuthorizationList, expected.AuthorizationList, AuthorizationTuple) &&
                EqualsIfIncluded(nameof(Transaction.IsServiceTransaction), actual.IsServiceTransaction, expected.IsServiceTransaction) &&
                EqualsIfIncluded(nameof(Transaction.PoolIndex), actual.PoolIndex, expected.PoolIndex);
        }

        public int GetHashCode(Transaction obj) => 0;

        private bool EqualsIfIncluded<T>(string propertyName, T actual, T expected) =>
            _excludedProperties.Contains(propertyName) || EqualityComparer<T>.Default.Equals(actual, expected);

        private bool EqualsIfIncluded(string propertyName, ReadOnlySpan<byte> actual, ReadOnlySpan<byte> expected) =>
            _excludedProperties.Contains(propertyName) || actual.SequenceEqual(expected);

        private bool EqualsIfIncluded<T>(string propertyName, T? actual, T? expected, IEqualityComparer<T> comparer)
            where T : class =>
            _excludedProperties.Contains(propertyName) || comparer.Equals(actual, expected);

        private bool ArraysEqualIfIncluded<T>(string propertyName, T[]? actual, T[]? expected, IEqualityComparer<T> comparer) =>
            _excludedProperties.Contains(propertyName) || ArraysEqual(actual, expected, comparer);

        private bool ByteArraysEqualIfIncluded(string propertyName, byte[]?[]? actual, byte[]?[]? expected) =>
            _excludedProperties.Contains(propertyName) || ByteArraysEqual(actual, expected);

        private bool NetworkWrappersEqualIfIncluded(object? actual, object? expected)
        {
            if (_excludedProperties.Contains(nameof(Transaction.NetworkWrapper)))
            {
                return true;
            }

            if (actual is null || expected is null)
            {
                return actual is null && expected is null;
            }

            if (actual is ShardBlobNetworkWrapper actualWrapper && expected is ShardBlobNetworkWrapper expectedWrapper)
            {
                return ShardBlobNetworkWrapperEqualityComparer.Instance.Equals(actualWrapper, expectedWrapper);
            }

            return actual.Equals(expected);
        }
    }

    private sealed class AccessListEqualityComparer : IEqualityComparer<AccessList>
    {
        public static AccessListEqualityComparer Instance { get; } = new();

        public bool Equals(AccessList? actual, AccessList? expected)
        {
            if (actual is null || expected is null)
            {
                return actual is null && expected is null;
            }

            if (actual.Count != expected.Count)
            {
                return false;
            }

            AccessList.Enumerator actualEntries = actual.GetEnumerator();
            AccessList.Enumerator expectedEntries = expected.GetEnumerator();

            while (true)
            {
                bool actualHasValue = actualEntries.MoveNext();
                bool expectedHasValue = expectedEntries.MoveNext();
                if (actualHasValue != expectedHasValue)
                {
                    return false;
                }

                if (!actualHasValue)
                {
                    return true;
                }

                (Address ActualAddress, AccessList.StorageKeysEnumerable ActualKeys) = actualEntries.Current;
                (Address ExpectedAddress, AccessList.StorageKeysEnumerable ExpectedKeys) = expectedEntries.Current;
                if (ActualAddress != ExpectedAddress || !StorageKeysEqual(ActualKeys, ExpectedKeys))
                {
                    return false;
                }
            }
        }

        public int GetHashCode(AccessList obj) => 0;

        private static bool StorageKeysEqual(AccessList.StorageKeysEnumerable actual, AccessList.StorageKeysEnumerable expected)
        {
            if (actual.Count != expected.Count)
            {
                return false;
            }

            using IEnumerator<UInt256> actualKeys = ((IEnumerable<UInt256>)actual).GetEnumerator();
            using IEnumerator<UInt256> expectedKeys = ((IEnumerable<UInt256>)expected).GetEnumerator();

            while (actualKeys.MoveNext())
            {
                if (!expectedKeys.MoveNext() || actualKeys.Current != expectedKeys.Current)
                {
                    return false;
                }
            }

            return !expectedKeys.MoveNext();
        }
    }

    private sealed class AuthorizationTupleEqualityComparer : IEqualityComparer<AuthorizationTuple>
    {
        public bool Equals(AuthorizationTuple? actual, AuthorizationTuple? expected)
        {
            if (actual is null || expected is null)
            {
                return actual is null && expected is null;
            }

            return actual.ChainId == expected.ChainId &&
                actual.CodeAddress == expected.CodeAddress &&
                actual.Nonce == expected.Nonce &&
                object.Equals(actual.AuthoritySignature, expected.AuthoritySignature) &&
                actual.Authority == expected.Authority;
        }

        public int GetHashCode(AuthorizationTuple obj) => 0;
    }

    private sealed class ShardBlobNetworkWrapperEqualityComparer : IEqualityComparer<ShardBlobNetworkWrapper>
    {
        public static ShardBlobNetworkWrapperEqualityComparer Instance { get; } = new();

        public bool Equals(ShardBlobNetworkWrapper? actual, ShardBlobNetworkWrapper? expected)
        {
            if (actual is null || expected is null)
            {
                return actual is null && expected is null;
            }

            return actual.Version == expected.Version &&
                ByteArraysEqual(actual.Blobs, expected.Blobs) &&
                ByteArraysEqual(actual.Commitments, expected.Commitments) &&
                ByteArraysEqual(actual.Proofs, expected.Proofs);
        }

        public int GetHashCode(ShardBlobNetworkWrapper obj) => 0;
    }

    private sealed class WithdrawalEqualityComparer : IEqualityComparer<Withdrawal>
    {
        public bool Equals(Withdrawal? actual, Withdrawal? expected)
        {
            if (actual is null || expected is null)
            {
                return actual is null && expected is null;
            }

            return actual.Index == expected.Index &&
                actual.ValidatorIndex == expected.ValidatorIndex &&
                actual.Address == expected.Address &&
                actual.AmountInGwei == expected.AmountInGwei;
        }

        public int GetHashCode(Withdrawal obj) => 0;
    }

    private sealed class BlockHeaderEqualityComparer(bool compareHash) : IEqualityComparer<BlockHeader>
    {
        public bool Equals(BlockHeader? actual, BlockHeader? expected)
        {
            if (actual is null || expected is null)
            {
                return actual is null && expected is null;
            }

            return actual.ParentHash == expected.ParentHash &&
                actual.UnclesHash == expected.UnclesHash &&
                actual.Author == expected.Author &&
                actual.Beneficiary == expected.Beneficiary &&
                actual.StateRoot == expected.StateRoot &&
                actual.TxRoot == expected.TxRoot &&
                actual.ReceiptsRoot == expected.ReceiptsRoot &&
                Equals(actual.Bloom, expected.Bloom) &&
                actual.Difficulty == expected.Difficulty &&
                actual.Number == expected.Number &&
                actual.GasUsed == expected.GasUsed &&
                actual.GasLimit == expected.GasLimit &&
                actual.Timestamp == expected.Timestamp &&
                BytesEqual(actual.ExtraData, expected.ExtraData) &&
                actual.MixHash == expected.MixHash &&
                actual.Nonce == expected.Nonce &&
                (!compareHash || actual.Hash == expected.Hash) &&
                actual.TotalDifficulty == expected.TotalDifficulty &&
                actual.BaseFeePerGas == expected.BaseFeePerGas &&
                actual.WithdrawalsRoot == expected.WithdrawalsRoot &&
                actual.ParentBeaconBlockRoot == expected.ParentBeaconBlockRoot &&
                actual.RequestsHash == expected.RequestsHash &&
                actual.BlockAccessListHash == expected.BlockAccessListHash &&
                actual.BlobGasUsed == expected.BlobGasUsed &&
                actual.ExcessBlobGas == expected.ExcessBlobGas &&
                actual.SlotNumber == expected.SlotNumber &&
                actual.IsPostMerge == expected.IsPostMerge;
        }

        public int GetHashCode(BlockHeader obj) => 0;
    }

    private sealed class BlockBodyEqualityComparer(bool compareHash) : IEqualityComparer<BlockBody>
    {
        private readonly IEqualityComparer<BlockHeader> _headerComparer = BlockHeader(compareHash);

        public bool Equals(BlockBody? actual, BlockBody? expected)
        {
            if (actual is null || expected is null)
            {
                return actual is null && expected is null;
            }

            return ArraysEqual(actual.Transactions, expected.Transactions, ForTransaction()) &&
                ArraysEqual(actual.Uncles, expected.Uncles, _headerComparer) &&
                ArraysEqual(actual.Withdrawals, expected.Withdrawals, Withdrawal);
        }

        public int GetHashCode(BlockBody obj) => 0;
    }

    private sealed class BlockEqualityComparer(bool compareHash) : IEqualityComparer<Block>
    {
        private readonly IEqualityComparer<BlockHeader> _headerComparer = BlockHeader(compareHash);
        private readonly IEqualityComparer<BlockBody> _bodyComparer = BlockBody(compareHash);

        public bool Equals(Block? actual, Block? expected)
        {
            if (actual is null || expected is null)
            {
                return actual is null && expected is null;
            }

            return _headerComparer.Equals(actual.Header, expected.Header) &&
                _bodyComparer.Equals(actual.Body, expected.Body) &&
                EqualityComparer<ReadOnlyBlockAccessList?>.Default.Equals(actual.BlockAccessList, expected.BlockAccessList) &&
                EqualityComparer<GeneratedBlockAccessList?>.Default.Equals(actual.GeneratedBlockAccessList, expected.GeneratedBlockAccessList) &&
                ByteArraysEqual(actual.ExecutionRequests, expected.ExecutionRequests) &&
                AccountChangesEqual(actual.AccountChanges, expected.AccountChanges) &&
                BytesEqual(actual.EncodedBlockAccessList, expected.EncodedBlockAccessList) &&
                ByteArraysEqual(actual.EncodedTransactions, expected.EncodedTransactions);
        }

        public int GetHashCode(Block obj) => 0;

        private static bool AccountChangesEqual(ArrayPoolList<AddressAsKey>? actual, ArrayPoolList<AddressAsKey>? expected)
        {
            if (actual is null || expected is null)
            {
                return actual is null && expected is null;
            }

            if (actual.Count != expected.Count)
            {
                return false;
            }

            for (int i = 0; i < expected.Count; i++)
            {
                if (!EqualityComparer<AddressAsKey>.Default.Equals(actual[i], expected[i]))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
