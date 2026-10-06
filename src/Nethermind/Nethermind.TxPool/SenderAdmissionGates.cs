// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;

namespace Nethermind.TxPool;

/// <summary>Striped per-sender locks that serialize MATCHA admission from the pending-set checks through insertion.</summary>
internal sealed class SenderAdmissionGates
{
    private const int GateCount = 64;
    private readonly Lock[] _gates = CreateGates();

    public Lock For(Address sender) => _gates[(sender.GetHashCode() & int.MaxValue) % GateCount];

    private static Lock[] CreateGates()
    {
        Lock[] gates = new Lock[GateCount];
        for (int i = 0; i < gates.Length; i++)
        {
            gates[i] = new Lock();
        }

        return gates;
    }
}
