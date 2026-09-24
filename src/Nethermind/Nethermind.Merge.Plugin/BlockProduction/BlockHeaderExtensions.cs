// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Merge.Plugin.BlockProduction;

internal static class BlockHeaderExtensions
{
    internal static PayloadAttributes GenerateSimulatedPayload(this BlockHeader parentHeader) =>
        new()
        {
            Timestamp = parentHeader.Timestamp + 1, // it doesn't matter, should be more than prior
            ParentBeaconBlockRoot = parentHeader.ParentHash, // it doesn't matter
            PrevRandao = parentHeader.ParentHash ?? Keccak.Zero, // it doesn't matter
            Withdrawals = [],
            SuggestedFeeRecipient = Address.Zero,
            // EIP-7843: once the fork is active the simulated block needs a slot, and without a CL the
            // next one after the parent's is the only grounded value. Like the timestamp above it must
            // be strictly greater than the parent's, or the attributes fail their own validation.
            SlotNumber = parentHeader.SlotNumber + 1
        };
}
