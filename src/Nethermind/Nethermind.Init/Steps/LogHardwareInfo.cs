// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.Core.Cpu;
#if !ZK_EVM
using Nethermind.Core.Crypto;
#endif
using Nethermind.Logging;
using ILogger = Nethermind.Logging.ILogger;

namespace Nethermind.Init.Steps;

public class LogHardwareInfo(ILogManager logManager) : IStep
{
    private readonly ILogger _logger = logManager.GetClassLogger<LogHardwareInfo>();

    public bool MustInitialize => false;

    public Task Execute(CancellationToken cancellationToken)
    {
#if !ZK_EVM
        LogExperimentalSve2KeccakStatus();
#endif
        if (!_logger.IsInfo) return Task.CompletedTask;

        try
        {
            CpuInfo? cpu = RuntimeInformation.GetCpuInfo();
            if (cpu is not null)
            {
                _logger.Info($"CPU: {cpu.ProcessorName} ({cpu.PhysicalCoreCount}C{cpu.LogicalCoreCount}T)");
            }
        }
        catch
        { }

        return Task.CompletedTask;
    }

#if !ZK_EVM
    private void LogExperimentalSve2KeccakStatus()
    {
        switch (KeccakHash.ExperimentalSve2KeccakState)
        {
            case KeccakHash.ExperimentalSve2KeccakStatus.Enabled when _logger.IsInfo:
                _logger.Info("Experimental SVE2 Keccak permutation enabled.");
                break;
            case KeccakHash.ExperimentalSve2KeccakStatus.Unsupported when _logger.IsWarn:
                _logger.Warn("Experimental SVE2 Keccak permutation was requested but is unsupported; falling back to the existing Keccak implementation.");
                break;
            case KeccakHash.ExperimentalSve2KeccakStatus.VerificationFailed when _logger.IsError:
                Exception? failure = KeccakHash.ExperimentalSve2KeccakFailure;
                if (failure is null)
                    _logger.Error("Experimental SVE2 Keccak permutation did not match scalar verification; falling back to the existing Keccak implementation.");
                else
                    _logger.Error("Experimental SVE2 Keccak permutation verification failed; falling back to the existing Keccak implementation.", failure);
                break;
        }
    }
#endif
}
