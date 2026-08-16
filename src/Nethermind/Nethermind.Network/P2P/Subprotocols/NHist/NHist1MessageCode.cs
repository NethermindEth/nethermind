// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.P2P.Subprotocols.NHist;

public static class NHist1MessageCode
{
    public const int GetChangesets = 0x02;
    public const int Changesets = 0x03;
    public const int Status = 0x04;
    public const int GetHistoryRows = 0x05;
    public const int HistoryRows = 0x06;
}
