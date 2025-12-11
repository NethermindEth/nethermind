// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Prometheus;

namespace Nethermind.Core;

public static class DevMetric
{
    // public static MetricFactory Factory = Prometheus.Metrics.WithCustomRegistry(Prometheus.Metrics.NewCustomRegistry()); // Custom registry does not publish by default
    public static MetricFactory Factory = Prometheus.Metrics.DefaultFactory;
}
