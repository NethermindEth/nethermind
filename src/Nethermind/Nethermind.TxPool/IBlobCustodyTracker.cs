// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.TxPool;

public interface IBlobCustodyTracker
{
    BlobCellMask CurrentMask { get; }
    event EventHandler<BlobCellMask>? CustodyChanged;
    bool Update(BlobCellMask mask);
}
