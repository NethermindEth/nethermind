// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Tools.Kute.FlowManager;
public class JsonRpcFlowManager : IJsonRpcFlowManager
{
    public JsonRpcFlowManager(int rps, bool unwrapBatch)
    {
        RequestsPerSecond = rps;
        ShouldUnwrapBatch = unwrapBatch;
    }
    public int RequestsPerSecond { get; }

    public bool ShouldUnwrapBatch { get; }
}
