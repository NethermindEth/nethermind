// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.State.Test.Runner
{
    public class StateTestTxTrace
    {
        //        public Stack<Dictionary<string, string>> StorageByDepth { get; } = new Stack<Dictionary<string, string>>();

        public StateTestTxTrace()
        {
            Entries = new List<StateTestTxTraceEntry>();
            Result = new StateTestTxTraceResult();
            State = new StateTestTxTraceState();
        }

        public StateTestTxTraceState State { get; set; }

        public StateTestTxTraceResult Result { get; set; }

        public List<StateTestTxTraceEntry> Entries { get; set; }
    }
}
