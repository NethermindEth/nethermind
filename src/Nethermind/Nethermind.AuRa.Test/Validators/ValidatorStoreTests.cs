// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;
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
                    (10UL, new[] {TestItem.AddressA})
                });

                yield return new TestCaseData(db, null, null, new[] { TestItem.AddressA }).SetCategory("InitializedDb").SetName("Db1 latest");
                yield return new TestCaseData(db, 11UL, null, new[] { TestItem.AddressA }).SetCategory("InitializedDb").SetName("Db1 11");
                yield return new TestCaseData(db, 10UL, null, Array.Empty<Address>()).SetCategory("InitializedDb").SetName("Db1 10");
                yield return new TestCaseData(db, 1UL, null, Array.Empty<Address>()).SetCategory("InitializedDb").SetName("Db1 1");

                db = CreateMemDbWithValidators(new[]
                {
                    (1UL, new[] {TestItem.AddressA}),
                    (5UL, new[] {TestItem.AddressA, TestItem.AddressB}),
                    (10UL, new[] {TestItem.AddressA, TestItem.AddressC})
                });

                yield return new TestCaseData(db, null, null, new[] { TestItem.AddressA, TestItem.AddressC }).SetCategory("InitializedDb").SetName("Db2 latest");
                yield return new TestCaseData(db, 11UL, null, new[] { TestItem.AddressA, TestItem.AddressC }).SetCategory("InitializedDb").SetName("Db2 11");
                yield return new TestCaseData(db, 10UL, null, new[] { TestItem.AddressA, TestItem.AddressB }).SetCategory("InitializedDb").SetName("Db2 10");
                yield return new TestCaseData(db, 6UL, null, new[] { TestItem.AddressA, TestItem.AddressB }).SetCategory("InitializedDb").SetName("Db2 6");
                yield return new TestCaseData(db, 5UL, null, new[] { TestItem.AddressA }).SetCategory("InitializedDb").SetName("Db2 5");
                yield return new TestCaseData(db, 2UL, null, new[] { TestItem.AddressA }).SetCategory("InitializedDb").SetName("Db2 2");
                yield return new TestCaseData(db, 1UL, null, Array.Empty<Address>()).SetCategory("InitializedDb").SetName("Db2 1");

                yield return new TestCaseData(CreateMemDbWithValidators(), null, new[]
                {
                    (5UL, new [] {TestItem.AddressB})
                }, new[] { TestItem.AddressB }).SetName("Add one").SetCategory("AddToStore");

                yield return new TestCaseData(CreateMemDbWithValidators(), null, new[]
                {
                    (5UL, new [] {TestItem.AddressB}),
                    (10UL, new [] {TestItem.AddressC}),
                    (15UL, new [] {TestItem.AddressC, TestItem.AddressA})
                }, new[] { TestItem.AddressC, TestItem.AddressA }).SetName("Add multiple, check latest").SetCategory("AddToStore");

                yield return new TestCaseData(CreateMemDbWithValidators(), 12UL, new[]
                {
                    (5UL, new [] {TestItem.AddressB}),
                    (10UL, new [] {TestItem.AddressC}),
                    (15UL, new [] {TestItem.AddressC, TestItem.AddressA})
                }, new[] { TestItem.AddressC }).SetName("Add multiple, check history").SetCategory("AddToStore");

                yield return new TestCaseData(db, null, new[]
                {
                    (20UL, new [] {TestItem.AddressB}),
                    (25UL, new [] {TestItem.AddressC}),
                }, new[] { TestItem.AddressC }).SetName("Add multiple, initialized db, check latest").SetCategory("AddToStore");

                yield return new TestCaseData(db, 12UL, new[]
                {
                    (20UL, new [] {TestItem.AddressB}),
                    (25UL, new [] {TestItem.AddressC}),
                }, new[] { TestItem.AddressA, TestItem.AddressC }).SetName("Add multiple, initialized db, check history").SetCategory("AddToStore");
            }
        }

        [TestCaseSource(nameof(ValidatorsTests))]
        public void validators_return_as_expected(IDb db, ulong? blockNumber, IEnumerable<(ulong FinalizingBlock, Address[] Validators)> validatorsToAdd, Address[] expectedValidators)
        {
            ValidatorStore store = new(db);
            if (validatorsToAdd is not null)
            {
                foreach ((ulong FinalizingBlock, Address[] Validators) validator in validatorsToAdd.OrderBy(static v => v.FinalizingBlock))
                {
                    store.SetValidators(validator.FinalizingBlock, validator.Validators);
                }
            }

            Assert.That(store.GetValidators(blockNumber), Is.EqualTo(expectedValidators));
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
                validators = new PendingValidators(10, Keccak.Zero, []);
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

            Assert.That(store.PendingValidators, Is.EqualTo(expectedValidators).UsingPropertiesComparer());
        }

        // regression test - was throwing NRE before
        [Test]
        public void GetValidators_throws_when_validator_info_missing_from_db()
        {
            MemDb db = new();
            db.Set(ValidatorStore.LatestFinalizedValidatorsBlockNumberKey, 10UL.ToBigEndianByteArrayWithoutLeadingZeros());
            ValidatorStore store = new(db);
            Assert.Throws<InvalidOperationException>(() => store.GetValidators());
        }

        private static MemDb CreateMemDbWithValidators(IEnumerable<(ulong FinalizingBlock, Address[] Validators)> validators = null)
        {
            static Hash256 GetKey(in ulong blockNumber) => Keccak.Compute("Validators" + blockNumber);

            validators ??= Array.Empty<(ulong FinalizingBlock, Address[] Validators)>();
            (ulong FinalizingBlock, Address[] Validators)[] ordered = validators.OrderByDescending(static v => v.FinalizingBlock).ToArray();

            MemDb memDb = new();

            for (int i = 0; i < ordered.Length; i++)
            {
                (ulong FinalizingBlock, Address[] Validators) current = ordered[i];
                (ulong FinalizingBlock, Address[] Validators) next = i + 1 < ordered.Length ? ordered[i + 1] : (ulong.MaxValue, Array.Empty<Address>());

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
