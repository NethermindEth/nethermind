// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.Kute.FlowManager;

interface IJsonRpcFlowManager
{
    int RequestsPerSecond { get; }
    bool ShouldUnwrapBatch { get; }
}
