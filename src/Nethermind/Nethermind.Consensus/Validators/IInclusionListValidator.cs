// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Consensus.Validators;

public interface IInclusionListValidator
{
    /// <summary>
    /// Validates that <paramref name="block"/> satisfies its inclusion list per
    /// <see href="https://eips.ethereum.org/EIPS/eip-7805">EIP-7805</see>.
    /// </summary>
    /// <param name="block">Block under validation; IL is <c>InclusionListTransactions</c>.</param>
    /// <param name="parentSenderState">Per-IL-sender nonce + balance captured at the parent
    /// block, before processing mutates the live worldstate.</param>
    bool ValidateInclusionList(Block block, IReadOnlyDictionary<AddressAsKey, AccountSnapshot> parentSenderState);
}
