// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Merge.Plugin.Data;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

[TestFixture]
public class ExecutionPayloadV4Tests
{
    [TestCaseSource(nameof(MalformedBlockAccessLists))]
    public void TryGetBlock_returns_decoding_error_for_malformed_block_access_list(byte[] blockAccessList)
    {
        ExecutionPayloadV4 payload = new()
        {
            BlockAccessList = blockAccessList,
            SlotNumber = 0,
            BlockNumber = 1,
            GasLimit = 30_000_000,
            ReceiptsRoot = Keccak.EmptyTreeHash,
            StateRoot = Keccak.EmptyTreeHash,
        };

        Result<Block> result = payload.TryGetBlock();

        Assert.That(result.Data, Is.Null);
        Assert.That(result.Error, Does.StartWith("Error decoding block access list:"));
    }

    private static IEnumerable<TestCaseData> MalformedBlockAccessLists()
    {
        yield return new TestCaseData(Array.Empty<byte>())
            .SetName("Empty_bytes");
        yield return new TestCaseData(new byte[] { 0xc1, 0xc0 })
            .SetName("Wrapped_empty_list");
    }
}
