// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.HealthChecks
{
    public interface INodeHealthService
    {
        CheckHealthResult CheckHealth();

        bool CheckClAlive();
    }
}
