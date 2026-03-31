// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Test.Runner
{
    public class StateTestTxTrace
    {
        public StateTestTxTraceState State { get; set; } = new();

        public StateTestTxTraceResult Result { get; set; } = new();

        public List<StateTestTxTraceEntry> Entries { get; set; } = new();
    }
}
