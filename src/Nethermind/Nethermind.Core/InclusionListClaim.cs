// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Core;

/// <summary>An EIP-8369 builder claim: the payload index at which an omitted Profile 2 candidate from the
/// inclusion list was tried.</summary>
/// <param name="TransactionHash">Keccak-256 of the inclusion-list entry's EIP-2718 bytes.</param>
/// <param name="TransactionIndex">The payload length when the builder tried to append the transaction, in
/// <c>[0, len(block.transactions)]</c>; larger values resolve to the end of the payload.</param>
public readonly record struct InclusionListClaim(Hash256 TransactionHash, ulong TransactionIndex);
