// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Threading.Tasks;

namespace Nethermind.Network.IP
{
    public interface IIPSource
    {
        Task<(bool Success, IPAddress Ip)> TryGetIP();
    }
}
