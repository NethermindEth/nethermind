// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.Store.Test;

internal class CollectingTrieHintSink : IPrewarmTrieHintSink
{
    public ConcurrentBag<Address> AccountHints { get; } = [];
    public ConcurrentBag<(Address, UInt256)> SlotHints { get; } = [];

    public void HintAccountWarm(Address address) => AccountHints.Add(address);

    public void HintSlotWarm(Address address, in UInt256 index) => SlotHints.Add((address, index));
}
