// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Ethereum.Test.Base
{
    public class PostStateJson
    {
        public IndexesJson Indexes { get; set; }
        public Keccak Hash { get; set; }
        public Keccak Logs { get; set; }
    }
}
