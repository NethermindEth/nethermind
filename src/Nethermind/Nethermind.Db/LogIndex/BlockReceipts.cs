// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Db.LogIndex;

public readonly record struct BlockReceipts(int BlockNumber, TxReceipt[] Receipts);
