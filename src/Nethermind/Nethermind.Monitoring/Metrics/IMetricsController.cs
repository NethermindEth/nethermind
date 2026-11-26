// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Monitoring.Metrics
{
    public interface IMetricsController
    {
        void RegisterMetrics([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicFields)] Type type);
        Task RunTimer(CancellationToken cancellationToken);
        void AddMetricsUpdateAction(Action callback);
    }
}
