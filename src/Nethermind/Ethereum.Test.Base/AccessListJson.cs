// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Ethereum.Test.Base
{
    public class AccessListItemJson
    {
        public Address Address { get; set; }

        public byte[][] StorageKeys { get; set; }
    }
}
