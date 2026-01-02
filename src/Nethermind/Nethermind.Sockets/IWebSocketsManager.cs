// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


namespace Nethermind.Sockets
{
    public interface IWebSocketsManager
    {
        void AddModule(IWebSocketsModule module, bool isDefault = false);
        IWebSocketsModule GetModule(string name);
    }
}
