// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only 

using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;

namespace Nethermind.RocksDbExtractor.Modules.Data.Providers
{
    internal static class Extensions
    {
        public static RlpStream AsRlpStream(this byte[] bytes)
            => bytes is null ? new RlpStream(Bytes.Empty) : new RlpStream(bytes);
    }
}
