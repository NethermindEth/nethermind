// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.State.Flat.ScopeProvider;

public class NoopTrieWarmer : ITrieWarmer
{
    public void PushSlotJob(ITrieWarmer.IStorageWarmer storageTree, in UInt256? index, int sequenceId, bool isWrite) { }

    public void PushAddressJob(ITrieWarmer.IAddressWarmer scope, Address? path, int sequenceId, bool isWrite) { }

    public void PushJobMulti(ITrieWarmer.IAddressWarmer scope, Address? path, ITrieWarmer.IStorageWarmer? storageTree, in UInt256? index, int sequenceId, bool isWrite) { }

    public void OnEnterScope() { }

    public void OnExitScope() { }
}
