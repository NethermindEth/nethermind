// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.P2P.ProtocolHandlers;

public interface IStaticProtocolInfo
{
    static abstract byte Version { get; }
    static abstract string Code { get; }
}
