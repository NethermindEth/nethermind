// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.Enr;

namespace Nethermind.Network.Discovery.Test;

internal sealed class TestEnrForkIdFilter(bool accept = true) : IEnrForkIdFilter
{
    public bool IsAcceptable(NodeRecord record) => accept;
}
