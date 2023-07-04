// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Int256;

namespace Ethereum.Test.Base
{
    public class AccountState
    {
        public byte[] Code { get; set; }
        public UInt256 Balance { get; set; }
        public UInt256 Nonce { get; set; }
        public Dictionary<UInt256, byte[]> Storage { get; set; }
    }
}
