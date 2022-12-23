// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Consumers.Receipts;
using Nethermind.DataMarketplace.Consumers.Receipts.Services;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Consumers.Test.Services.Receipts
{
    public class ReceiptRequestValidatorTests
    {
        private IReceiptRequestValidator _validator;

        [SetUp]
        public void Setup()
        {
            _validator = new ReceiptRequestValidator(LimboLogs.Instance);
        }

        [Test]
        public void given_units_range_from_0_to_0_and_1_unpaid_units_request_should_be_valid()
        {
            var unpaidUnits = 1;
            var consumedUnits = 0;
            var purchasedUnits = 100;
            var unitsRange = new UnitsRange(0, 0);
            var receiptRequest = CreateRequest(unitsRange);

            var isValid = _validator.IsValid(receiptRequest, unpaidUnits, consumedUnits, purchasedUnits);

            isValid.Should().BeTrue();
        }

        [Test]
        public void given_units_range_from_0_to_9_and_10_unpaid_units_request_should_be_valid()
        {
            var unpaidUnits = 11;
            var consumedUnits = 0;
            var purchasedUnits = 100;
            var unitsRange = new UnitsRange(0, 9);
            var receiptRequest = CreateRequest(unitsRange);

            var isValid = _validator.IsValid(receiptRequest, unpaidUnits, consumedUnits, purchasedUnits);

            isValid.Should().BeTrue();
        }

        [Test]
        public void given_units_range_from_0_to_9_and_9_unpaid_units_request_should_be_invalid()
        {
            var unpaidUnits = 9;
            var consumedUnits = 0;
            var purchasedUnits = 100;
            var unitsRange = new UnitsRange(0, 9);
            var receiptRequest = CreateRequest(unitsRange);

            var isValid = _validator.IsValid(receiptRequest, unpaidUnits, consumedUnits, purchasedUnits);

            isValid.Should().BeFalse();
        }

        [Test]
        public void given_units_range_from_5_to_9_and_6_unpaid_units_request_should_be_valid()
        {
            var unpaidUnits = 6;
            var consumedUnits = 0;
            var purchasedUnits = 100;
            var unitsRange = new UnitsRange(5, 9);
            var receiptRequest = CreateRequest(unitsRange);

            var isValid = _validator.IsValid(receiptRequest, unpaidUnits, consumedUnits, purchasedUnits);

            isValid.Should().BeTrue();
        }

        [Test]
        public void given_previous_range_from_0_to_9_and_actual_range_from_10_to_19_and_10_unpaid_units_request_should_be_valid()
        {
            var unpaidUnits = 10;
            var consumedUnits = 0;
            var purchasedUnits = 100;
            var previousUnitsRange = new UnitsRange(0, 9);
            var unitsRange = new UnitsRange(10, 19);
            var previousReceipt = new DataDeliveryReceiptToMerge(previousUnitsRange, null);
            var receiptRequest = CreateRequest(unitsRange, new[] { previousReceipt });

            var isValid = _validator.IsValid(receiptRequest, unpaidUnits, consumedUnits, purchasedUnits);

            isValid.Should().BeTrue();
        }

        [Test]
        public void given_previous_range_from_10_to_19_and_actual_range_from_0_to_9_and_10_unpaid_units_request_should_be_valid()
        {
            var unpaidUnits = 10;
            var consumedUnits = 0;
            var purchasedUnits = 100;
            var previousUnitsRange = new UnitsRange(10, 19);
            var unitsRange = new UnitsRange(0, 9);
            var previousReceipt = new DataDeliveryReceiptToMerge(previousUnitsRange, null);
            var receiptRequest = CreateRequest(unitsRange, new[] { previousReceipt });

            var isValid = _validator.IsValid(receiptRequest, unpaidUnits, consumedUnits, purchasedUnits);

            isValid.Should().BeTrue();
        }

        [Test]
        public void given_previous_range_from_0_to_9_and_actual_range_from_15_to_19_and_5_unpaid_units_request_should_be_valid()
        {
            var unpaidUnits = 10;
            var consumedUnits = 0;
            var purchasedUnits = 100;
            var previousUnitsRange = new UnitsRange(15, 19);
            var unitsRange = new UnitsRange(0, 9);
            var previousReceipt = new DataDeliveryReceiptToMerge(previousUnitsRange, null);
            var receiptRequest = CreateRequest(unitsRange, new[] { previousReceipt });

            var isValid = _validator.IsValid(receiptRequest, unpaidUnits, consumedUnits, purchasedUnits);

            isValid.Should().BeTrue();
        }

        [Test]
        public void given_previous_ranges_from_0_to_3_and_4_to_7_and_8_to_11_and_actual_range_from_0_to_19_and_8_unpaid_units_request_should_be_valid()
        {
            var unpaidUnits = 8;
            var consumedUnits = 0;
            var purchasedUnits = 100;
            var unitsRange = new UnitsRange(0, 19);
            var previousReceipts = new[]
            {
                new DataDeliveryReceiptToMerge(new UnitsRange(0, 3), null),
                new DataDeliveryReceiptToMerge(new UnitsRange(4, 7), null),
                new DataDeliveryReceiptToMerge(new UnitsRange(8, 11), null),
            };
            var receiptRequest = CreateRequest(unitsRange, previousReceipts);

            var isValid = _validator.IsValid(receiptRequest, unpaidUnits, consumedUnits, purchasedUnits);

            isValid.Should().BeTrue();
        }

        [Test]
        public void given_previous_ranges_from_0_to_27_and_actual_range_from_21_to_32_and_12_unpaid_units_request_should_be_valid()
        {
            var unpaidUnits = 5;
            var consumedUnits = 0;
            var purchasedUnits = 100;
            var unitsRange = new UnitsRange(0, 32);
            var previousReceipts = new[]
            {
                new DataDeliveryReceiptToMerge(new UnitsRange(0, 27), null)
            };
            var receiptRequest = CreateRequest(unitsRange, previousReceipts);

            var isValid = _validator.IsValid(receiptRequest, unpaidUnits, consumedUnits, purchasedUnits);

            isValid.Should().BeTrue();
        }

        [Test]
        public void given_previous_range_from_21_to_31_and_actual_range_from_21_to_34_and_3_unpaid_units_request_should_be_valid()
        {
            var unpaidUnits = 3;
            var consumedUnits = 0;
            var purchasedUnits = 100;
            var unitsRange = new UnitsRange(21, 34);
            var previousReceipts = new[]
            {
                new DataDeliveryReceiptToMerge(new UnitsRange(21, 31), null)
            };
            var receiptRequest = CreateRequest(unitsRange, previousReceipts);

            var isValid = _validator.IsValid(receiptRequest, unpaidUnits, consumedUnits, purchasedUnits);

            isValid.Should().BeTrue();
        }

        [Test]
        public void given_previous_range_from_0_to_27_and_actual_range_from_21_to_32_and_3_unpaid_units_request_should_be_valid()
        {
            var unpaidUnits = 5;
            var consumedUnits = 0;
            var purchasedUnits = 100;
            var unitsRange = new UnitsRange(21, 32);
            var previousReceipts = new[]
            {
                new DataDeliveryReceiptToMerge(new UnitsRange(0, 27), null)
            };
            var receiptRequest = CreateRequest(unitsRange, previousReceipts);

            var isValid = _validator.IsValid(receiptRequest, unpaidUnits, consumedUnits, purchasedUnits);

            isValid.Should().BeTrue();
        }

        [Test]
        public void given_previous_range_from_0_to_9_and_actual_range_from_9_to_15_and_10_unpaid_units_request_should_be_valid()
        {
            var unpaidUnits = 10;
            var consumedUnits = 0;
            var purchasedUnits = 100;
            var previousUnitsRange = new UnitsRange(0, 9);
            var unitsRange = new UnitsRange(9, 15);
            var previousReceipt = new DataDeliveryReceiptToMerge(previousUnitsRange, null);
            var receiptRequest = CreateRequest(unitsRange, new[] { previousReceipt });

            var isValid = _validator.IsValid(receiptRequest, unpaidUnits, consumedUnits, purchasedUnits);

            isValid.Should().BeTrue();
        }

        [Test]
        public void given_previous_ranges_from_0_to_11_and_actual_range_from_0_to_22_and_11_unpaid_units_request_should_be_valid()
        {
            var unpaidUnits = 11;
            var consumedUnits = 0;
            var purchasedUnits = 100;
            var unitsRange = new UnitsRange(0, 22);
            var previousReceipts = new[]
            {
                new DataDeliveryReceiptToMerge(new UnitsRange(0, 11), null)
            };
            var receiptRequest = CreateRequest(unitsRange, previousReceipts);

            var isValid = _validator.IsValid(receiptRequest, unpaidUnits, consumedUnits, purchasedUnits);

            isValid.Should().BeTrue();
        }

        private static DataDeliveryReceiptRequest CreateRequest(UnitsRange unitsRange,
            IEnumerable<DataDeliveryReceiptToMerge> previousReceipts = null)
            => new DataDeliveryReceiptRequest(1, Keccak.Zero, unitsRange, false,
                previousReceipts ?? Enumerable.Empty<DataDeliveryReceiptToMerge>());
    }
}
