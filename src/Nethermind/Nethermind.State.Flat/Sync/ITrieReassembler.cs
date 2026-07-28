// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.State.Flat.Sync;

public interface ITrieReassembler
{
    Hash256? TryReassemble(IReadOnlyCollection<Hash256> updatedStorageAccounts);
}
