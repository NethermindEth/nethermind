// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    public class TransactionSubstateTests
    {
        [Test]
        public void should_return_revert_sentinel_for_unknown_selector_with_abi_like_layout()
        {
            // All-zero selector is not Error(string) or Panic(uint256). The old generic fallback
            // block (removed to match Geth's UnpackRevert default) would have decoded this into
            // "0x0506070809". Now it must return the Revert sentinel like any other unknown selector.
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
            TransactionSubstate transactionSubstate = new(readOnlyMemory,
                0,
                new JournalSet<Address>(Address.EqualityComparer),
                new JournalCollection<LogEntry>(),
                true,
                true);
            transactionSubstate.Error.Should().Be(TransactionSubstate.Revert);
        }

        [Test]
        public void should_return_proper_revert_error_when_there_is_exception()
        {
            // Unknown selector — custom error; Error must be the Revert sentinel, not the hex bytes.
            byte[] data = { 0x05, 0x06, 0x07, 0x08, 0x09 };
            ReadOnlyMemory<byte> readOnlyMemory = new(data);
            TransactionSubstate transactionSubstate = new(readOnlyMemory,
                0,
                new JournalSet<Address>(Address.EqualityComparer),
                new JournalCollection<LogEntry>(),
                true,
                true);
            transactionSubstate.Error.Should().Be(TransactionSubstate.Revert);
        }

        [Test]
        public void should_return_revert_sentinel_when_error_function_selector_has_malformed_encoding()
        {
            // Starts with Error(string) selector but the ABI offset field is 1 (not 32), so
            // GetErrorMessage correctly rejects it. Error must fall back to the Revert sentinel.
            byte[] data = TransactionSubstate.ErrorFunctionSelector.Concat(Bytes.FromHexString("0x00000001000000000000000000000000000000000000000012a9d65e7d180cfcf3601b6d00000000000000000000000000000000000000000000000000000001000276a400000000000000000000000000000000000000000000000000000000000000a0000000000000000000000000000000000000000000000000000000000000006a000000000300000000000115859c410282f6600012efb47fcfcad4f96c83d4ca676842fb03ef20a4770000000015f762bdaa80f6d9dc5518ff64cb7ba5717a10dabc4be3a41acd2c2f95ee22000012a9d65e7d180cfcf3601b6df0000000000000185594dac7eb0828ff000000000000000000000000")).ToArray();
            ReadOnlyMemory<byte> readOnlyMemory = new(data);
            TransactionSubstate transactionSubstate = new(readOnlyMemory,
                0,
                new JournalSet<Address>(Address.EqualityComparer),
                new JournalCollection<LogEntry>(),
                true,
                true);
            transactionSubstate.Error.Should().Be(TransactionSubstate.Revert);
        }


        [Test]
        public void should_return_revert_sentinel_for_custom_error_selector()
        {
            // keccak4("ActionFailed()") = 0x080a1c27 — unknown selector, no ABI decoding possible.
            // Error must be the Revert sentinel so only data, not message, carries the raw bytes.
            byte[] data = { 0x08, 0x0a, 0x1c, 0x27 };
            ReadOnlyMemory<byte> readOnlyMemory = new(data);
            TransactionSubstate transactionSubstate = new(
                readOnlyMemory,
                0,
                new JournalSet<Address>(Address.EqualityComparer),
                new JournalCollection<LogEntry>(),
                true,
                true);
            transactionSubstate.Error.Should().Be(TransactionSubstate.Revert);
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

            // Malformed Error(string) payload — GetErrorMessage rejects it, falls back to Revert sentinel.
            yield return (new byte[] { 0x08, 0xc3, 0x79, 0xa0, 0xFF }, TransactionSubstate.Revert);
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

            // Unknown selector — custom error; falls back to Revert sentinel.
            yield return (new byte[] { 0xf0, 0x28, 0x8c, 0x28 }, TransactionSubstate.Revert);
        }

        [Test]
        [TestCaseSource(nameof(ErrorFunctionTestCases))]
        [TestCaseSource(nameof(PanicFunctionTestCases))]
        public void should_return_proper_revert_error_when_using_special_functions((byte[] data, string expected) tc)
        {
            // See: https://docs.soliditylang.org/en/latest/control-structures.html#revert
            ReadOnlyMemory<byte> readOnlyMemory = new(tc.data);
            TransactionSubstate transactionSubstate = new(
                readOnlyMemory,
                0,
                new JournalSet<Address>(Address.EqualityComparer),
                new JournalCollection<LogEntry>(),
                true,
                true);

            transactionSubstate.Error.Should().Be(tc.expected);
        }

        [Test]
        public void should_return_proper_revert_error_when_revert_custom_error()
        {
            // FailedOp(uint256,string) has an unknown selector — custom error. Error must be the
            // Revert sentinel; the raw ABI bytes belong only in the data field of the RPC response.
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
                readOnlyMemory,
                0,
                new JournalSet<Address>(Address.EqualityComparer),
                new JournalCollection<LogEntry>(),
                true,
                true);
            transactionSubstate.Error.Should().Be(TransactionSubstate.Revert);
        }
    }
}
