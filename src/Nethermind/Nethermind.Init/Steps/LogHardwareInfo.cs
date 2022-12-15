// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HardwareInformation;
using HardwareInformation.Information;
using Nethermind.Api;
using ILogger = Nethermind.Logging.ILogger;

namespace Nethermind.Init.Steps;

public class LogHardwareInfo : IStep
{
    private readonly ILogger _logger;

    public bool MustInitialize => false;

    public LogHardwareInfo(INethermindApi api)
    {
        _logger = api.LogManager.GetClassLogger();
    }

    public Task Execute(CancellationToken cancellationToken)
    {
        if (!_logger.IsInfo) return Task.CompletedTask;

        MachineInformation info = MachineInformationGatherer.GatherInformation();

        CPU cpu = info.Cpu;
        _logger.Info($"CPU: {cpu.Name} ({cpu.PhysicalCores}C{cpu.LogicalCores}T), {cpu.NormalClockSpeed}Mhz - {cpu.MaxClockSpeed}Mhz");

        return Task.CompletedTask;
    }
}
