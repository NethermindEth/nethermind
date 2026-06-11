// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Blockchain.Synchronization
{
    public interface IPeerWithSatelliteProtocol
    {
        void RegisterSatelliteProtocol<T>(string protocol, T protocolHandler) where T : class;
        bool TryGetSatelliteProtocol<T>(string protocol, [NotNullWhen(true)] out T? protocolHandler) where T : class;
    }
}
