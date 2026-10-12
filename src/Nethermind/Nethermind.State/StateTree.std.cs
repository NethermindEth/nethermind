// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Serialization.Rlp;

namespace Nethermind.State;

public partial class StateTree
{
    public static readonly Rlp EmptyAccountRlp = _decoder.Encode(Account.TotallyEmpty);
}
