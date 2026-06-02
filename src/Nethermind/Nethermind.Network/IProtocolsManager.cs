// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Stats.Model;

namespace Nethermind.Network
{
    public interface IProtocolsManager
    {
        void AddSupportedCapability(Capability capability);
        void RemoveSupportedCapability(Capability capability);
        int GetHighestProtocolVersion(string protocol);
    }
}
