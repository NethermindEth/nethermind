// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;

namespace Nethermind.Network.P2P
{
    public interface IPingSender
    {
        Task<bool> SendPing();
    }
}
