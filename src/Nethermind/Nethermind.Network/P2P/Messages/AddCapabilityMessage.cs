// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P.Messages
{
    public class AddCapabilityMessage : P2PMessage
    {
        public override string Protocol => "p2p";
        public override int PacketType => P2PMessageCode.AddCapability;
        public Capability Capability { get; }

        public AddCapabilityMessage(Capability capability)
        {
            Capability = capability;
        }
    }
}
