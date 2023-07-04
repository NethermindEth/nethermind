// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.EthStats.Messages.Models;
// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global

namespace Nethermind.EthStats.Messages
{
    public class HelloMessage : IMessage
    {
        public string? Id { get; set; }
        public string Secret { get; }
        public Info Info { get; }

        public HelloMessage(string secret, Info info)
        {
            Secret = secret;
            Info = info;
        }
    }
}
