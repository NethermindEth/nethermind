// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Consensus
{
    public interface IGossipPolicy
    {
        public bool ShouldDiscardBlocks => false;

        public bool CanGossipBlocks { get; }

        public bool ShouldGossipBlock(BlockHeader header) => CanGossipBlocks;

        public bool ShouldDisconnectGossipingNodes { get; }
    }

    public class ShouldNotGossip : IGossipPolicy
    {
        private ShouldNotGossip() { }

        public static ShouldNotGossip Instance { get; } = new();

        public bool CanGossipBlocks => false;
        public bool ShouldDisconnectGossipingNodes => true;
    }

    public class ShouldGossip : IGossipPolicy
    {
        private ShouldGossip() { }

        public static IGossipPolicy Instance { get; } = new ShouldGossip();

        public bool CanGossipBlocks => true;
        public bool ShouldDisconnectGossipingNodes => false;
    }

    public static class Policy
    {
        public static IGossipPolicy NoBlockGossip { get; } = ShouldNotGossip.Instance;

        public static IGossipPolicy FullGossip { get; } = ShouldGossip.Instance;
    }
}
