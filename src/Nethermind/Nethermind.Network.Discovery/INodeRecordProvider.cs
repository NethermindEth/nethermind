// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.Enr;

namespace Nethermind.Network.Discovery;

public interface INodeRecordProvider
{
    public NodeRecord Current { get; }
}
