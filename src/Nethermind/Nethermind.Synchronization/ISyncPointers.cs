// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Synchronization;

public interface ISyncPointers
{
    long? LowestInsertedBodyNumber { get; set; }
    long? LowestInsertedReceiptBlockNumber { get; set; }
}
