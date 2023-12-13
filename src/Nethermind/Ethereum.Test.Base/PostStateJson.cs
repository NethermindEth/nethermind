// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Ethereum.Test.Base
{
    public class PostStateJson
    {
        public IndexesJson Indexes { get; set; }
        public Hash256 Hash { get; set; }
        public Hash256 Logs { get; set; }
    }
}
