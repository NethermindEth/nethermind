// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.State;

internal class VerkleTransientStorageProvider : PartialStorageProviderBase
{
    public VerkleTransientStorageProvider(ILogManager? logManager)
        : base(logManager) { }

    protected override byte[] GetCurrentValue(in StorageCell storageCell) =>
        TryGetCachedValue(storageCell, out byte[]? bytes) ? bytes! : _zeroValue;

    public override void ClearStorage(Address address)
    {
        throw new NotSupportedException("Verkle Trees does not support deletion of data from the tree");
    }
}
