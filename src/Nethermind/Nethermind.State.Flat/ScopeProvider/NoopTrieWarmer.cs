// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.State.Flat.ScopeProvider;

public class NoopTrieWarmer : ITrieWarmer
{
    public void PushSlotJob(ITrieWarmer.IStorageWarmer storageTree, in UInt256? index, int sequenceId) { }

    public void PushAddressJob(ITrieWarmer.IAddressWarmer scope, Address? path, int sequenceId) { }

    public void OnEnterScope() { }

    public void OnExitScope() { }
}
