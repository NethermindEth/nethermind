// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Evm.CodeAnalysis;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    public class TransactionSubstateTests
    {
        [Test]
        public void should_return_proper_revert_error_when_there_is_no_exception()
        {
            byte[] data =
            {
                0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x20,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x5,
                0x05, 0x06, 0x07, 0x08, 0x09
            };
            ReadOnlyMemory<byte> readOnlyMemory = new(data);
            TransactionSubstate transactionSubstate = new((CodeInfo.Empty, readOnlyMemory),
                0,
                new ArraySegment<Address>(),
                Array.Empty<LogEntry>(),
                true,
                true);
            transactionSubstate.Error.Should().Be("\u0005\u0006\u0007\u0008\t");
        }

        [Test]
        public void should_return_proper_revert_error_when_there_is_exception()
        {
            byte[] data = { 0x05, 0x06, 0x07, 0x08, 0x09 };
            ReadOnlyMemory<byte> readOnlyMemory = new(data);
            TransactionSubstate transactionSubstate = new((CodeInfo.Empty, readOnlyMemory),
                0,
                new ArraySegment<Address>(),
                Array.Empty<LogEntry>(),
                true,
                true);
            transactionSubstate.Error.Should().Be("\u0005\u0006\u0007\u0008\t");
        }

        [Test]
        public void should_return_weird_revert_error_when_there_is_exception()
        {
            byte[] data = TransactionSubstate.ErrorFunctionSelector.Concat(Bytes.FromHexString("0x00000001000000000000000000000000000000000000000012a9d65e7d180cfcf3601b6d00000000000000000000000000000000000000000000000000000001000276a400000000000000000000000000000000000000000000000000000000000000a0000000000000000000000000000000000000000000000000000000000000006a000000000300000000000115859c410282f6600012efb47fcfcad4f96c83d4ca676842fb03ef20a4770000000015f762bdaa80f6d9dc5518ff64cb7ba5717a10dabc4be3a41acd2c2f95ee22000012a9d65e7d180cfcf3601b6df0000000000000185594dac7eb0828ff000000000000000000000000")).ToArray();
            ReadOnlyMemory<byte> readOnlyMemory = new(data);
            TransactionSubstate transactionSubstate = new((CodeInfo.Empty, readOnlyMemory),
                0,
                new ArraySegment<Address>(),
                Array.Empty<LogEntry>(),
                true,
                true);
            transactionSubstate.Error.Should().Be("0x08c379a000000001000000000000000000000000000000000000000012a9d65e7d180cfcf3601b6d00000000000000000000000000000000000000000000000000000001000276a400000000000000000000000000000000000000000000000000000000000000a0000000000000000000000000000000000000000000000000000000000000006a000000000300000000000115859c410282f6600012efb47fcfcad4f96c83d4ca676842fb03ef20a4770000000015f762bdaa80f6d9dc5518ff64cb7ba5717a10dabc4be3a41acd2c2f95ee22000012a9d65e7d180cfcf3601b6df0000000000000185594dac7eb0828ff000000000000000000000000");
        }

        [Test]
        [Description($"Replace with {nameof(should_return_proper_revert_error_when_revert_custom_error)} once fixed")]
        public void should_return_proper_revert_error_when_revert_custom_error_badly_implemented()
        {
            // See: https://github.com/NethermindEth/nethermind/issues/6024

            string hex =
                "0x220266b600000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000040000000000000000000000000000000000000000000000000000000000000001741413231206469646e2774207061792070726566756e64000000000000000000";
            byte[] data = Bytes.FromHexString(hex);
            ReadOnlyMemory<byte> readOnlyMemory = new(data);
            TransactionSubstate transactionSubstate = new(
                (CodeInfo.Empty, readOnlyMemory),
                0,
                new ArraySegment<Address>(),
                Array.Empty<LogEntry>(),
                true,
                true);
            transactionSubstate.Error.Should().Be("0x220266b600000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000040000000000000000000000000000000000000000000000000000000000000001741413231206469646e2774207061792070726566756e64000000000000000000");
        }

        private static IEnumerable<(byte[], string)> ErrorFunctionTestCases()
        {
            yield return (
                new byte[]
                {
                    0x08, 0xc3, 0x79, 0xa0, // Function selector for Error(string)
                    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x20, // Data offset
                    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x1a, // String length
                    0x4e, 0x6f, 0x74, 0x20, 0x65, 0x6e, 0x6f, 0x75, 0x67, 0x68, 0x20, 0x45, 0x74, 0x68, 0x65, 0x72, 0x20, 0x70, 0x72, 0x6f, 0x76, 0x69, 0x64, 0x65, 0x64, 0x2e, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // String data
                },
                "Not enough Ether provided.");

            yield return (
                new byte[]
                {
                    0x08, 0xc3, 0x79, 0xa0,
                    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x20,
                    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x12,
                    0x52, 0x65, 0x71, 0x3a, 0x3a, 0x55, 0x6e, 0x41, 0x75, 0x74, 0x68, 0x41, 0x75, 0x64, 0x69, 0x74, 0x6f, 0x72, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
                },
                "Req::UnAuthAuditor");

            // Invalid case
            yield return (new byte[] { 0x08, 0xc3, 0x79, 0xa0, 0xFF }, "0x08c379a0ff");
        }

        private static IEnumerable<(byte[], string)> PanicFunctionTestCases()
        {
            yield return (
                new byte[]
                {
                    0x4e, 0x48, 0x7b, 0x71, // Function selector for Panic(uint256)
                    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 // Panic code 0x0
                },
                "generic panic");

            yield return (
                new byte[]
                {
                    0x4e, 0x48, 0x7b, 0x71, // Function selector for Panic(uint256)
                    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x22 // Panic code 0x22
                },
                "invalid encoded storage byte array accessed");

            yield return (
                new byte[]
                {
                    0x4e, 0x48, 0x7b, 0x71,
                    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF // Unknown panic code
                },
                "unknown panic code (0xff)");

            // Invalid case
            yield return (new byte[] { 0xf0, 0x28, 0x8c, 0x28 }, "0xf0288c28");
        }

        [Test]
        [TestCaseSource(nameof(ErrorFunctionTestCases))]
        [TestCaseSource(nameof(PanicFunctionTestCases))]
        public void should_return_proper_revert_error_when_using_special_functions((byte[] data, string expected) tc)
        {
            // See: https://docs.soliditylang.org/en/latest/control-structures.html#revert
            ReadOnlyMemory<byte> readOnlyMemory = new(tc.data);
            TransactionSubstate transactionSubstate = new(
                (CodeInfo.Empty, readOnlyMemory),
                0,
                new ArraySegment<Address>(),
                Array.Empty<LogEntry>(),
                true,
                true);

            transactionSubstate.Error.Should().Be(tc.expected);
        }

        [Test]
        [Ignore("Badly implemented")]
        public void should_return_proper_revert_error_when_revert_custom_error()
        {
            byte[] data =
            {
                0x22, 0x02, 0x66, 0xb6, // Keccak of `FailedOp(uint256,string)` == 0x220266b6
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x40, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x17,
                0x41, 0x41, 0x32, 0x31, 0x20, 0x64, 0x69, 0x64, 0x6e, 0x27, 0x74, 0x20, 0x70, 0x61, 0x79, 0x20, 0x70, 0x72, 0x65, 0x66, 0x75, 0x6e, 0x64, // "AA21 didn't pay prefund"
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
            };
            ReadOnlyMemory<byte> readOnlyMemory = new(data);
            TransactionSubstate transactionSubstate = new(
                (CodeInfo.Empty, readOnlyMemory),
                0,
                new ArraySegment<Address>(),
                Array.Empty<LogEntry>(),
                true,
                true);
            transactionSubstate.Error.Should().Be("0x41413231206469646e2774207061792070726566756e64");
        }
    }
}
