// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Blockchain.Visitors
{
    [Flags]
    public enum BlockVisitOutcome
    {
        None,
        Suggest = 1,
        StopVisiting = 2,
        All = 3
    }
}
