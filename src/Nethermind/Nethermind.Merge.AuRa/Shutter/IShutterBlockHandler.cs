// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Merge.AuRa.Shutter;
public interface IShutterBlockHandler
{
    void OnBlockProcessed(Block head, TxReceipt[] receipts);
}
