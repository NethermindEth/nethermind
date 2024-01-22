// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
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

        try
        {
            var cpu = Cpu.RuntimeInformation.GetCpuInfo();
            if (cpu is not null)
            {
                _logger.Info($"CPU: {cpu.ProcessorName} ({cpu.PhysicalCoreCount}C{cpu.LogicalCoreCount}T)");
            }
        }
        catch
        { }

        return Task.CompletedTask;
    }
}
