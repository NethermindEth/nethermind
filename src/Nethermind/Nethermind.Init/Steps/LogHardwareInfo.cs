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
        StringBuilder sb = new();
        sb.AppendLine();
        sb.AppendLine($" CPU: {cpu.Name} ({cpu.PhysicalCores}C{cpu.LogicalCores}T)");
        sb.AppendLine($" Clocks: {cpu.NormalClockSpeed}Mhz - {cpu.MaxClockSpeed}Mhz");

        string flags = string.Join(", ", new List<string>()
        {
            cpu.FeatureFlagsOne.ToString(),
            cpu.FeatureFlagsTwo.ToString(),
            cpu.ExtendedFeatureFlagsF7One.ToString(),
            cpu.ExtendedFeatureFlagsF7Two.ToString(),
            cpu.ExtendedFeatureFlagsF7Three.ToString(),
        }.Where((str) => !string.IsNullOrWhiteSpace(str)));

        sb.Append($" Flags: {flags}");

        _logger.Info(sb.ToString());

        return Task.CompletedTask;
    }
}
