// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    public class TransactionSubstateTests
    {
        [Test]
        public void should_return_proper_revert_error_when_there_is_no_exception()
        {
            byte[] data = {0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x20,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x5,
                0x05, 0x06, 0x07, 0x08, 0x09};
            ReadOnlyMemory<byte> readOnlyMemory = new(data);
            TransactionSubstate transactionSubstate = new(readOnlyMemory,
                0,
                new ArraySegment<Address>(),
                new LogEntry[] { },
                true,
                true);
            transactionSubstate.Error.Should().Be("Reverted 0x0506070809");
        }

        [Test]
        public void should_return_proper_revert_error_when_there_is_exception()
        {
            byte[] data = { 0x05, 0x06, 0x07, 0x08, 0x09 };
            ReadOnlyMemory<byte> readOnlyMemory = new(data);
            TransactionSubstate transactionSubstate = new(readOnlyMemory,
                0,
                new ArraySegment<Address>(),
                new LogEntry[] { },
                true,
                true);
            transactionSubstate.Error.Should().Be("Reverted 0x0506070809");
        }

        [Test]
        [Description($"Replace with {nameof(should_return_proper_revert_error_when_revert_custom_error)} once fixed")]
        public void should_return_proper_revert_error_when_revert_custom_error_badly_implemented()
        {
            // See: https://github.com/NethermindEth/nethermind/issues/6024

            string input =
                "0x220266b600000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000040000000000000000000000000000000000000000000000000000000000000001741413231206469646e2774207061792070726566756e64000000000000000000";
            byte[] data = Bytes.FromHexString(input);
            ReadOnlyMemory<byte> readOnlyMemory = new(data);
            TransactionSubstate transactionSubstate = new(
                readOnlyMemory,
                0,
                new ArraySegment<Address>(),
                new LogEntry[] { },
                true,
                true);
            transactionSubstate.Error.Should().Be($"Reverted {input}");
        }

        [Test]
        [Ignore("Badly implemented")]
        public void should_return_proper_revert_error_when_revert_custom_error()
        {
            byte[] data = {
                0x22, 0x02, 0x66, 0xb6, // Keccak of 'FailedOp(uint256,string) == 0x220266b6'
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x40, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x17,
                0x41, 0x41, 0x32, 0x31, 0x20, 0x64, 0x69, 0x64, 0x6e, 0x27, 0x74, 0x20, 0x70, 0x61, 0x79, 0x20, 0x70, 0x72, 0x65, 0x66, 0x75, 0x6e, 0x64, // "AA21 didn't pay prefund"
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
            ReadOnlyMemory<byte> readOnlyMemory = new(data);
            TransactionSubstate transactionSubstate = new(
                readOnlyMemory,
                0,
                new ArraySegment<Address>(),
                new LogEntry[] { },
                true,
                true);
            transactionSubstate.Error.Should().Be("Reverted 0x41413231206469646e2774207061792070726566756e64");
        }

        [Test]
        [Ignore("Badly implemented in several clients (geth, erigon, Nethermind)")]
        public void should_return_proper_revert_error_eip_140_test_case()
        {
            // See: https://eips.ethereum.org/EIPS/eip-140

            byte[] data = {
                0x6c, 0x72, 0x65, 0x76, 0x65, 0x72, 0x74, 0x65, 0x64, 0x20, 0x64, 0x61, 0x74, 0x61, // "lreverted data'
                0x60, 0x00, 0x55, 0x7f,
                0x72, 0x65, 0x76, 0x65, 0x72, 0x74, 0x20, 0x6d, 0x65, 0x73, 0x73, 0x61, 0x67, 0x65, // "revert message"
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x60, 0x00, 0x52, 0x60, 0x0e, 0x60, 0x00, 0xfd,
            };
            ReadOnlyMemory<byte> readOnlyMemory = new(data);
            TransactionSubstate transactionSubstate = new(
                readOnlyMemory,
                0,
                new ArraySegment<Address>(),
                new LogEntry[] { },
                true,
                true);
            transactionSubstate.Error.Should().Be("Reverted 0x726576657274206d657373616765");
        }
    }
}
