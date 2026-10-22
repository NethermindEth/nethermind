// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;

namespace Nethermind.Core.ServiceStopper;

public interface IServiceStopper
{
    void AddStoppable(IStoppableService stoppableService);
    Task StopAllServices();
}
