// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.EthStats.Messages.Models;

// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global

namespace Nethermind.EthStats.Messages
{
    public class HistoryMessage(IEnumerable<Block> history) : IMessage
    {
        public string? Id { get; set; }
        public IEnumerable<Block> History { get; } = history;
    }
}
