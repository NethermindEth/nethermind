// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.EthStats.Messages.Models;
// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global

namespace Nethermind.EthStats.Messages
{
    public class BlockMessage : IMessage
    {
        public string? Id { get; set; }

        public Block Block { get; }

        public BlockMessage(Block block)
        {
            Block = block;
        }
    }
}
