// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Crypto;

namespace Nethermind.Mev.Data
{
    public class TxsResults : Dictionary<Keccak, TxResult>
    {
        public TxsResults(IDictionary<Keccak, TxResult> dictionary) : base(dictionary) { }
    }
}
