// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;

namespace Nethermind.BeaconNode.Services
{
    public interface INodeStart
    {
        Task InitializeNodeAsync();
    }
}
