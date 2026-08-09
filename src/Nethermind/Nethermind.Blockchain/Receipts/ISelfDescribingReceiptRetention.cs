// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Blockchain.Receipts;

public interface ISelfDescribingReceiptRetention
{
    bool TryRetainSelfDescribing(Block block);
}
