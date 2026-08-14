// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Network;
using Nethermind.Network.Enr;
using Nethermind.State;
using Nethermind.State.Flat.History;

namespace Nethermind.Init.FlatHistory;

/// <summary>Advertises this node as a history server in its own ENR, so consumers can find it through discovery
/// instead of having to be told about it as a static peer. A node that is not serving contributes nothing, which
/// leaves the record - and the discovery traffic it rides on - unchanged for everyone else. The advertisement
/// answers what the nhist status message would, minus the watermark, which moves too often to sit in a record.</summary>
public sealed class NHistRecordContributor(IHistoryServer historyServer, HistoryRowFormat rowFormat) : INodeRecordContributor
{
    public bool TryGetEntry([NotNullWhen(true)] out EnrContentEntry? entry)
    {
        if (!historyServer.CanServe)
        {
            entry = null;
            return false;
        }

        entry = new NHistEntry(rowFormat.FormatVersion, historyServer.CanServeFullClone);
        return true;
    }
}
