// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Prometheus;

namespace Nethermind.Core;

public static class DevMetric
{
    public static MetricFactory Factory =
        Environment.GetEnvironmentVariable("ASHRAF_DEV") != null
            ? Prometheus.Metrics.DefaultFactory
            : Prometheus.Metrics.WithCustomRegistry(Prometheus.Metrics.NewCustomRegistry());
}
