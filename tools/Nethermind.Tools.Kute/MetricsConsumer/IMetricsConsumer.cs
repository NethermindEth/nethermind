// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.Kute.MetricsConsumer;

public interface IMetricsConsumer
{
    Task ConsumeMetrics(Metrics metrics);
}

public enum MetricsOutputFormatter
{
    Report, Json,
}
