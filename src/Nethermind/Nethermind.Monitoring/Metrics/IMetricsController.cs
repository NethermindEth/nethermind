// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Monitoring.Metrics
{
    public interface IMetricsController
    {
        void RegisterMetrics(Type type);
        void StartUpdating(Action metricsUpdated);
        void StopUpdating();
        void AddMetricsUpdateAction(Action callback);
        void ForceUpdate();
    }
}
