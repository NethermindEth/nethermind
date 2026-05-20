// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Metric;

namespace Nethermind.JsonRpc;

internal sealed class JsonRpcMetricLabels(string method, bool success) : IMetricLabels
{
    private readonly string[] _labels = [method, success ? "success" : "fail"];

    public string[] Labels => _labels;
}
