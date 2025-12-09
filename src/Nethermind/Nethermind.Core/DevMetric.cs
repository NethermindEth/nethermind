// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Prometheus;

namespace Nethermind.Core;

public static class DevMetric
{
    public static CollectorRegistry _customRegistry = Prometheus.Metrics.NewCustomRegistry();
    public static MetricFactory Factory = Prometheus.Metrics.WithCustomRegistry(_customRegistry);
}
