// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Network.Enr;

namespace Nethermind.Network;

/// <summary>Contributes an entry to this node's own ENR, so a component can advertise what it offers without the
/// record provider having to know about it. Only session-stable facts belong in a record: the entry is read once
/// per record build, and a value that changes would keep forcing a re-sign.</summary>
public interface INodeRecordContributor
{
    bool TryGetEntry([NotNullWhen(true)] out EnrContentEntry? entry);
}
