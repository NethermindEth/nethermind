// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Serialization.Rlp;

namespace Nethermind.State;

// Lazy, so the guest runs no class constructor for this type.
public partial class StateTree
{
    public static Rlp EmptyAccountRlp => field ??= _decoder.Encode(Account.TotallyEmpty);
}
