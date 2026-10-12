// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Serialization.Rlp.Eip7928;

public partial class BlockAccessListDecoder
{
    private static readonly RlpLimit _accountsLimit = new(Eip7928Constants.MaxAccounts, "", ReadOnlyMemory<char>.Empty);
}
