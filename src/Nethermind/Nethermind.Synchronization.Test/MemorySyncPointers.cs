// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Synchronization.Test;

public class MemorySyncPointers : ISyncPointers
{
    public long? LowestInsertedBodyNumber { get; set; }
    public long? LowestInsertedReceiptBlockNumber { get; set; }
}
