//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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

                var db = CreateMemDbWithValidators(new []
                {
                    (10L, new[] {TestItem.AddressA})
                });

                yield return new TestCaseData(db, null, null, new[] {TestItem.AddressA}).SetCategory("InitializedDb").SetName("Db1 latest");
                yield return new TestCaseData(db, 11L, null, new[] {TestItem.AddressA}).SetCategory("InitializedDb").SetName("Db1 11");
                yield return new TestCaseData(db, 10L, null, Array.Empty<Address>()).SetCategory("InitializedDb").SetName("Db1 10");
                yield return new TestCaseData(db, 1L, null, Array.Empty<Address>()).SetCategory("InitializedDb").SetName("Db1 1");

                db = CreateMemDbWithValidators(new []
                {
                    (1L, new[] {TestItem.AddressA}),
                    (5L, new[] {TestItem.AddressA, TestItem.AddressB}),
                    (10L, new[] {TestItem.AddressA, TestItem.AddressC})
                });

                yield return new TestCaseData(db, null, null, new[] {TestItem.AddressA, TestItem.AddressC}).SetCategory("InitializedDb").SetName("Db2 latest");
                yield return new TestCaseData(db, 11L, null, new[] {TestItem.AddressA, TestItem.AddressC}).SetCategory("InitializedDb").SetName("Db2 11");
                yield return new TestCaseData(db, 10L, null, new[] {TestItem.AddressA, TestItem.AddressB}).SetCategory("InitializedDb").SetName("Db2 10");
                yield return new TestCaseData(db, 6L, null, new[] {TestItem.AddressA, TestItem.AddressB}).SetCategory("InitializedDb").SetName("Db2 6");
                yield return new TestCaseData(db, 5L, null, new[] {TestItem.AddressA}).SetCategory("InitializedDb").SetName("Db2 5");
                yield return new TestCaseData(db, 2L, null, new[] {TestItem.AddressA}).SetCategory("InitializedDb").SetName("Db2 2");
                yield return new TestCaseData(db, 1L, null, Array.Empty<Address>()).SetCategory("InitializedDb").SetName("Db2 1");

                yield return new TestCaseData(CreateMemDbWithValidators(), null, new []
                {
                    (5L, new [] {TestItem.AddressB})
                }, new [] {TestItem.AddressB}).SetName("Add one").SetCategory("AddToStore");

                yield return new TestCaseData(CreateMemDbWithValidators(), null, new []
                {
                    (5L, new [] {TestItem.AddressB}),
                    (10L, new [] {TestItem.AddressC}),
                    (15L, new [] {TestItem.AddressC, TestItem.AddressA})
                }, new [] {TestItem.AddressC, TestItem.AddressA}).SetName("Add multiple, check latest").SetCategory("AddToStore");

                yield return new TestCaseData(CreateMemDbWithValidators(), 12L, new []
                {
                    (5L, new [] {TestItem.AddressB}),
                    (10L, new [] {TestItem.AddressC}),
                    (15L, new [] {TestItem.AddressC, TestItem.AddressA})
                }, new [] {TestItem.AddressC}).SetName("Add multiple, check history").SetCategory("AddToStore");

                yield return new TestCaseData(db, null, new []
                {
                    (20L, new [] {TestItem.AddressB}),
                    (25L, new [] {TestItem.AddressC}),
                }, new[] {TestItem.AddressC}).SetName("Add multiple, initialized db, check latest").SetCategory("AddToStore");

                yield return new TestCaseData(db, 12L, new []
                {
                    (20L, new [] {TestItem.AddressB}),
                    (25L, new [] {TestItem.AddressC}),
                }, new[] {TestItem.AddressA, TestItem.AddressC}).SetName("Add multiple, initialized db, check history").SetCategory("AddToStore");
            }
        }

        [TestCaseSource(nameof(ValidatorsTests))]
        public void validators_return_as_expected(IDb db, long? blockNumber, IEnumerable<(long FinalizingBlock, Address[] Validators)> validatorsToAdd, Address[] expectedValidators)
        {
            var store = new ValidatorStore(db);
            if (validatorsToAdd != null)
            {
                foreach (var validator in validatorsToAdd.OrderBy(v => v.FinalizingBlock))
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

                var validators = new PendingValidators(100, Keccak.EmptyTreeHash, TestItem.Addresses.Take(5).ToArray());
                yield return new TestCaseData(new MemDb(), validators, true, validators);

                var db = new MemDb();
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
            var store = new ValidatorStore(db);
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
            var ordered = validators.OrderByDescending(v => v.FinalizingBlock).ToArray();

            var memDb = new MemDb();

            for (int i = 0; i < ordered.Length; i++)
            {
                var current = ordered[i];
                var next = i + 1 < ordered.Length ? ordered[i + 1] : (-1, Array.Empty<Address>());
                var validatorInfo = new ValidatorInfo(current.FinalizingBlock, next.FinalizingBlock, current.Validators);

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
