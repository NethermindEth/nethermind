// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;

namespace Nethermind.Test.Runner
{
    public class StateTestTxTraceResult
    {
        public byte[] Output { get; set; }
        public long GasUsed { get; set; }
        public double Time { get; set; }
        public string Error { get; set; }
    }
}
