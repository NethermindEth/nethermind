// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core;
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
    }
}
