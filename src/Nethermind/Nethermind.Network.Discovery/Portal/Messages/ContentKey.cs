// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Network.Discovery.Portal;

public class ContentKey: IUnion
{
    [Selector(0)]
    public ValueHash256? HeaderKey { get; set; }

    [Selector(1)]
    public ValueHash256? BodyKey { get; set; }

    [Selector(2)]
    public ValueHash256? ReceiptKey { get; set; }
}

// Not for SSZ deserialization as the selector depends on the content key
// Its just here to fit IKademlia... which may or may not be a good idea.
public class ContentContent
{
    public BlockHeader? Header { get; set; }
    public BlockBody? Body { get; set; }
    public TxReceipt[]? Receipts { get; set; }
}
