// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Crypto;

namespace Nethermind.Mev.Data
{
    public class TxsResults : Dictionary<Commitment, TxResult>
    {
        public TxsResults(IDictionary<Commitment, TxResult> dictionary) : base(dictionary) { }
    }
}
