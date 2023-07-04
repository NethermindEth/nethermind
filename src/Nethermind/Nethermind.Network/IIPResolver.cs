// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Threading.Tasks;

namespace Nethermind.Network
{
    public interface IIPResolver
    {
        IPAddress LocalIp { get; }
        IPAddress ExternalIp { get; }
        Task Initialize();
    }
}
