using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Core.Timers;
using Nethermind.Logging;

namespace Nethermind.Init.Steps;

[RunnerStepDependencies(typeof(MigrateConfigs))]
public class SetupGCConfig : IStep
{
    IInitConfig _initConfig;
    private ILogger _logger;

    public SetupGCConfig(INethermindApi api)
    {
        _initConfig = api.Config<IInitConfig>();
        _logger = api.LogManager.GetClassLogger();
    }

    public Task Execute(CancellationToken cancellationToken)
    {
        if (_initConfig.GCMemoryPressureHintMb != 0)
        {
            _logger.Info($"Specifying unmanaged memory pressure of {_initConfig.GCMemoryPressureHintMb} MB");
            GC.AddMemoryPressure(_initConfig.GCMemoryPressureHintMb * 1024);
        }

        if (_initConfig.TriggerGCIntervalSec != null)
        {
            // Not blocking to run in background
#pragma warning disable CS4014
            FunctionalTimer.RunEvery(TimeSpan.FromSeconds((double)_initConfig.TriggerGCIntervalSec!), cancellationToken, (token =>
#pragma warning restore CS4014
            {
                GCMemoryInfo memoryInfo = GC.GetGCMemoryInfo();

                if (memoryInfo.HeapSizeBytes > _initConfig.TriggerGCMinHeapMb * 1024)
                {
                    _logger.Info($"Triggering GC");
                    GC.Collect();
                }
                else
                {
                    if (_logger.IsTrace) _logger.Trace($"Not triggering GC as threshold not met. Heap: {memoryInfo.HeapSizeBytes}, Threshold: {_initConfig.TriggerGCMinHeapMb * 1000}");
                }
            }));
        }

        return Task.CompletedTask;
    }
}
