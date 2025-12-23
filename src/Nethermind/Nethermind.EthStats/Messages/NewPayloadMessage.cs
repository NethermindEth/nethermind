// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.EthStats.Messages.Models;

namespace Nethermind.EthStats.Messages
{
    public class NewPayloadMessage : IMessage
    {
        public string? Id { get; set; }
        public NewPayloadBlock Block { get; }

        public NewPayloadMessage(NewPayloadBlock block)
        {
            Block = block;
        }
    }
}
