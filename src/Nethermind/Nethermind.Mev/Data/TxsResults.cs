// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Crypto;

namespace Nethermind.Mev.Data
{
    public class TxsResults : Dictionary<Hash256, TxResult>
    {
        public TxsResults(IDictionary<Hash256, TxResult> dictionary) : base(dictionary) { }
    }
}
