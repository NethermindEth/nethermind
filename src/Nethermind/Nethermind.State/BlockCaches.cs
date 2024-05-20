// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.State;

public class BlockCaches
{
    public IDictionary<StorageCell, byte[]> StorageCache { get; } = new NonBlocking.ConcurrentDictionary<StorageCell, byte[]>(Environment.ProcessorCount, 4096);
    public IDictionary<AddressAsKey, Account> StateCache { get; } = new NonBlocking.ConcurrentDictionary<AddressAsKey, Account>(Environment.ProcessorCount, 4096);
}

