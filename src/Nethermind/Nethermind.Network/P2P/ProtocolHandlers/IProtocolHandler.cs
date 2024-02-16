// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Network.Rlpx;
using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P.ProtocolHandlers
{
    public interface IProtocolHandler : IDisposable
    {
        string Name { get; }
        byte ProtocolVersion { get; }
        string ProtocolCode { get; }
        int MessageIdSpaceSize { get; }
        void Init();
        void HandleMessage(Packet message);
        void DisconnectProtocol(DisconnectReason disconnectReason, string details);
        event EventHandler<ProtocolInitializedEventArgs> ProtocolInitialized;
        event EventHandler<ProtocolEventArgs> SubprotocolRequested;
    }

    public interface IProtocolHandlerFactory
    {
        int ProtocolPriority { get; }
        int MessageIdSpaceSize { get; }
        IProtocolHandler Create(ISession session);
    }

    public static class ProtocolPriorities
    {
        public const int P2P = 1;
        public const int Sync = 10;
        public const int Satellite = 100;
    }
}
