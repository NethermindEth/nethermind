// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;
using Nethermind.Db.Blooms;
using NUnit.Framework;

namespace Nethermind.AuRa.Test.Validators
{
    public class ValidatorStoreTests
    {
        public static IEnumerable ValidatorsTests
        {
            get
            {
                yield return new TestCaseData(CreateMemDbWithValidators(), null, null, Array.Empty<Address>()).SetCategory("InitializedDb").SetName("EmptyDb");

                MemDb db = CreateMemDbWithValidators(new[]
                {
                    (10L, new[] {TestItem.AddressA})
                });

                yield return new TestCaseData(db, null, null, new[] { TestItem.AddressA }).SetCategory("InitializedDb").SetName("Db1 latest");
                yield return new TestCaseData(db, 11L, null, new[] { TestItem.AddressA }).SetCategory("InitializedDb").SetName("Db1 11");
                yield return new TestCaseData(db, 10L, null, Array.Empty<Address>()).SetCategory("InitializedDb").SetName("Db1 10");
                yield return new TestCaseData(db, 1L, null, Array.Empty<Address>()).SetCategory("InitializedDb").SetName("Db1 1");

                db = CreateMemDbWithValidators(new[]
                {
                    (1L, new[] {TestItem.AddressA}),
                    (5L, new[] {TestItem.AddressA, TestItem.AddressB}),
                    (10L, new[] {TestItem.AddressA, TestItem.AddressC})
                });

                yield return new TestCaseData(db, null, null, new[] { TestItem.AddressA, TestItem.AddressC }).SetCategory("InitializedDb").SetName("Db2 latest");
                yield return new TestCaseData(db, 11L, null, new[] { TestItem.AddressA, TestItem.AddressC }).SetCategory("InitializedDb").SetName("Db2 11");
                yield return new TestCaseData(db, 10L, null, new[] { TestItem.AddressA, TestItem.AddressB }).SetCategory("InitializedDb").SetName("Db2 10");
                yield return new TestCaseData(db, 6L, null, new[] { TestItem.AddressA, TestItem.AddressB }).SetCategory("InitializedDb").SetName("Db2 6");
                yield return new TestCaseData(db, 5L, null, new[] { TestItem.AddressA }).SetCategory("InitializedDb").SetName("Db2 5");
                yield return new TestCaseData(db, 2L, null, new[] { TestItem.AddressA }).SetCategory("InitializedDb").SetName("Db2 2");
                yield return new TestCaseData(db, 1L, null, Array.Empty<Address>()).SetCategory("InitializedDb").SetName("Db2 1");

                yield return new TestCaseData(CreateMemDbWithValidators(), null, new[]
                {
                    (5L, new [] {TestItem.AddressB})
                }, new[] { TestItem.AddressB }).SetName("Add one").SetCategory("AddToStore");

                yield return new TestCaseData(CreateMemDbWithValidators(), null, new[]
                {
                    (5L, new [] {TestItem.AddressB}),
                    (10L, new [] {TestItem.AddressC}),
                    (15L, new [] {TestItem.AddressC, TestItem.AddressA})
                }, new[] { TestItem.AddressC, TestItem.AddressA }).SetName("Add multiple, check latest").SetCategory("AddToStore");

                yield return new TestCaseData(CreateMemDbWithValidators(), 12L, new[]
                {
                    (5L, new [] {TestItem.AddressB}),
                    (10L, new [] {TestItem.AddressC}),
                    (15L, new [] {TestItem.AddressC, TestItem.AddressA})
                }, new[] { TestItem.AddressC }).SetName("Add multiple, check history").SetCategory("AddToStore");

                yield return new TestCaseData(db, null, new[]
                {
                    (20L, new [] {TestItem.AddressB}),
                    (25L, new [] {TestItem.AddressC}),
                }, new[] { TestItem.AddressC }).SetName("Add multiple, initialized db, check latest").SetCategory("AddToStore");

                yield return new TestCaseData(db, 12L, new[]
                {
                    (20L, new [] {TestItem.AddressB}),
                    (25L, new [] {TestItem.AddressC}),
                }, new[] { TestItem.AddressA, TestItem.AddressC }).SetName("Add multiple, initialized db, check history").SetCategory("AddToStore");
            }
        }

        [TestCaseSource(nameof(ValidatorsTests))]
        public void validators_return_as_expected(IDb db, long? blockNumber, IEnumerable<(long FinalizingBlock, Address[] Validators)> validatorsToAdd, Address[] expectedValidators)
        {
            ValidatorStore store = new(db);
            if (validatorsToAdd is not null)
            {
                foreach ((long FinalizingBlock, Address[] Validators) validator in validatorsToAdd.OrderBy(v => v.FinalizingBlock))
                {
                    store.SetValidators(validator.FinalizingBlock, validator.Validators);
                }
            }

            store.GetValidators(blockNumber).Should().BeEquivalentTo(expectedValidators);
        }

        public static IEnumerable PendingValidatorsTests
        {
            get
            {
                yield return new TestCaseData(new MemDb(), null, false, null);
                yield return new TestCaseData(new MemDb(), null, true, null);

                PendingValidators validators = new(100, Keccak.EmptyTreeHash, TestItem.Addresses.Take(5).ToArray());
                yield return new TestCaseData(new MemDb(), validators, true, validators);

                MemDb db = new();
                db.Set(ValidatorStore.PendingValidatorsKey, Rlp.Encode(validators).Bytes);
                yield return new TestCaseData(db, null, false, validators);
                yield return new TestCaseData(db, null, true, null);

                db.Set(ValidatorStore.PendingValidatorsKey, Rlp.Encode(validators).Bytes);
                validators = new PendingValidators(10, Keccak.Zero, Array.Empty<Address>());
                yield return new TestCaseData(db, validators, true, validators);
            }
        }

        [TestCaseSource(nameof(PendingValidatorsTests))]
        public void pending_validators_return_as_expected(IDb db, PendingValidators validators, bool setValidators, PendingValidators expectedValidators)
        {
            ValidatorStore store = new(db);
            if (setValidators)
            {
                store.PendingValidators = validators;
            }

            store.PendingValidators.Should().BeEquivalentTo(expectedValidators);
        }


        private static MemDb CreateMemDbWithValidators(IEnumerable<(long FinalizingBlock, Address[] Validators)> validators = null)
        {
            Keccak GetKey(in long blockNumber) => Keccak.Compute("Validators" + blockNumber);

            validators ??= Array.Empty<(long FinalizingBlock, Address[] Validators)>();
            (long FinalizingBlock, Address[] Validators)[] ordered = validators.OrderByDescending(v => v.FinalizingBlock).ToArray();

            MemDb memDb = new();

            for (int i = 0; i < ordered.Length; i++)
            {
                (long FinalizingBlock, Address[] Validators) current = ordered[i];
                (long FinalizingBlock, Address[] Validators) next = i + 1 < ordered.Length ? ordered[i + 1] : (-1, Array.Empty<Address>());
                ValidatorInfo validatorInfo = new(current.FinalizingBlock, next.FinalizingBlock, current.Validators);

                if (i == 0)
                {
                    memDb.Set(ValidatorStore.LatestFinalizedValidatorsBlockNumberKey, current.FinalizingBlock.ToBigEndianByteArrayWithoutLeadingZeros());
                }

                memDb.Set(GetKey(current.FinalizingBlock), Rlp.Encode(validatorInfo).Bytes);
            }

            return memDb;
        }
    }
}
