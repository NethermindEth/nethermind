// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Serialization.Rlp.Eip7928;

public partial class SlotChangesDecoder
{
    private static readonly RlpLimit _txLimit = new(Eip7928Constants.MaxTxs, "", ReadOnlyMemory<char>.Empty);
}
