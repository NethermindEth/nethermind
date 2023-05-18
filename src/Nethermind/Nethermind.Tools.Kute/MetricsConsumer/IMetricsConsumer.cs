// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.Kute.MetricsConsumer;

public interface IMetricsConsumer
{
    void ConsumeMetrics(Metrics metrics);
}

public enum MetricConsumerStrategy
{
    Report, Json
}
