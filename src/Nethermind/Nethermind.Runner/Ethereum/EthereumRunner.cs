// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Evm;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.Int256;
using Nethermind.Db;



namespace Nethermind.Runner.Ethereum
{


    public class CommonPatternsAnalyzer
    {
        public Dictionary<(byte, byte), ulong> PatternCounts { get; set; }

        private readonly ILogger _logger;
        public CommonPatternsAnalyzer(ILogger logger)
        {
            _logger = logger;
            PatternCounts = new Dictionary<(byte, byte), ulong>();
        }


        public void LogStats()
        {
            var sortedStats = PatternCounts.OrderByDescending(entry => entry.Value);
            _logger.Info($"Instruction1, Instruction2, count");
            foreach (var entry in sortedStats)
            {
                ulong frequency = entry.Value;
                _logger.Info($"{((Instruction)entry.Key.Item1).GetName()}, {((Instruction)entry.Key.Item2).GetName()}, {frequency}");
            }
        }
        public bool AddOrIncreaseCount((byte, byte) pattern)
        {

            ulong freq = 0;
            if (!PatternCounts.TryGetValue(pattern, out freq))
            {
                PatternCounts.Add(pattern, 1);
            }
            else
            {
                PatternCounts[pattern] = (ulong)freq + (ulong)1;
            }
            return true;
        }


        /*
       //var countMap = new Dictionary<(Instruction, Instruction), int>();
       public class ByteArrayComparer : IEqualityComparer<byte[]>
       {
           public bool Equals(byte[] left, byte[] right)
           {
               if (left == null || right == null)
               {
                   return left == right;
               }
               return left.SequenceEqual(right);
           }
           public int GetHashCode(byte[] obj)
           {

               ushort length = (ushort)((obj[0] << 8) | obj[1]);
               return (int)length;
           }
       }
       */
    }
    public class EthereumRunner
    {
        private readonly INethermindApi _api;

        private readonly ILogger _logger;

        public EthereumRunner(INethermindApi api)
        {
            _api = api;
            _logger = api.LogManager.GetClassLogger();
        }

        public async Task Start(CancellationToken cancellationToken)
        {
            if (_logger.IsDebug) _logger.Debug("Initializing Ethereum");


            EthereumStepsLoader stepsLoader = new EthereumStepsLoader(GetStepsAssemblies(_api));
            EthereumStepsManager stepsManager = new EthereumStepsManager(stepsLoader, _api, _api.LogManager);
            await stepsManager.InitializeAll(cancellationToken);
            var codedb = _api.DbProvider?.CodeDb;
            Stats(codedb);
            string infoScreen = ThisNodeInfo.BuildNodeInfoScreen();
            if (_logger.IsInfo) _logger.Info(infoScreen);
        }

        private IEnumerable<Assembly> GetStepsAssemblies(INethermindApi api)
        {
            yield return typeof(IStep).Assembly;
            yield return GetType().Assembly;
            IEnumerable<IInitializationPlugin> enabledInitializationPlugins =
                _api.Plugins.OfType<IInitializationPlugin>().Where(p => p.ShouldRunSteps(api));

            foreach (IInitializationPlugin initializationPlugin in enabledInitializationPlugins)
            {
                yield return initializationPlugin.GetType().Assembly;
            }
        }


        public void StripAndAddByteCode(ReadOnlySpan<byte> machineCode, CommonPatternsAnalyzer InstructionStats)
        {
            byte[] window = new byte[2];

            byte prev = (byte)machineCode[0];
            for (ushort i = 0; i < machineCode.Length; i++)
            {
                Instruction opcode = (Instruction)machineCode[i];
                ushort pc = i;
                if (opcode is > Instruction.PUSH0 and <= Instruction.PUSH32)
                {
                    ushort immediatesCount = opcode - Instruction.PUSH0;
                    i += immediatesCount;
                }

                if (i >= 1)
                {
                    InstructionStats.AddOrIncreaseCount((prev, machineCode[pc]));
                    prev = machineCode[pc];
                }
            }
        }

        private ReadOnlySpan<byte> StripMetadata(ReadOnlySpan<byte> code)
        {
            if (code.Length > 2)
            {

                ushort length = (ushort)((code[code.Length - 2] << 8) | code[code.Length - 1]);
                if (code.Length > length + 2)
                {
                    return code.Slice(0, code.Length - (length + 2));
                }
            }
            return code;

        }
        public void Stats(IDb codedb)
        {

            _logger.Info("************ Starting Op Code Stats Task*******************");
            CommonPatternsAnalyzer commonPatternAnalyzer = new(_logger);
            int len = codedb.GetAllValues().Count();
            int onePercent = len / 100;
            var c = 0;

            _logger.Info($"items : {len}. 1 percent: {len / 100}");
            foreach (ReadOnlySpan<byte> code in codedb.GetAllValues())
            {
                if (c % onePercent == 0)
                {
                    _logger.Info($"progress done: {c}. percent: {((c * 100) / len)}");
                }
                c++;
                //_logger.Info(code.Length.ToString());
                var strippedMetadataCode = StripMetadata(code);
                StripAndAddByteCode(strippedMetadataCode, commonPatternAnalyzer);
            }
            _logger.Info("added code");
            commonPatternAnalyzer.LogStats();
            _logger.Info("finished stats");
        }

        public async Task StopAsync()
        {

            var codedb = _api.DbProvider?.CodeDb;

            //int keyOfLength32 = 0;

            //var countMap = new Dictionary<(Instruction, Instruction), int>();





            Stop(() => _api.SessionMonitor?.Stop(), "Stopping session monitor");
            Stop(() => _api.SyncModeSelector?.Stop(), "Stopping session sync mode selector");
            Task discoveryStopTask = Stop(() => _api.DiscoveryApp?.StopAsync(), "Stopping discovery app");
            Task blockProducerTask = Stop(() => _api.BlockProducerRunner?.StopAsync(), "Stopping block producer");
            Task syncPeerPoolTask = Stop(() => _api.SyncPeerPool?.StopAsync(), "Stopping sync peer pool");
            Task peerPoolTask = Stop(() => _api.PeerPool?.StopAsync(), "Stopping peer pool");
            Task peerManagerTask = Stop(() => _api.PeerManager?.StopAsync(), "Stopping peer manager");
            Task synchronizerTask = Stop(() => _api.Synchronizer?.StopAsync(), "Stopping synchronizer");
            Task blockchainProcessorTask = Stop(() => _api.BlockchainProcessor?.StopAsync(), "Stopping blockchain processor");
            Task rlpxPeerTask = Stop(() => _api.RlpxPeer?.Shutdown(), "Stopping rlpx peer");
            await Task.WhenAll(discoveryStopTask, rlpxPeerTask, peerManagerTask, synchronizerTask, syncPeerPoolTask, peerPoolTask, blockchainProcessorTask, blockProducerTask);

            foreach (INethermindPlugin plugin in _api.Plugins)
            {
                await Stop(async () => await plugin.DisposeAsync(), $"Disposing plugin {plugin.Name}");
            }

            while (_api.DisposeStack.Count != 0)
            {
                IAsyncDisposable disposable = _api.DisposeStack.Pop();
                await Stop(async () => await disposable.DisposeAsync(), $"Disposing {disposable}");
            }


            Stop(() => _api.DbProvider?.Dispose(), "Closing DBs");


            if (_logger.IsInfo) _logger.Info("All DBs closed.");

            if (_logger.IsInfo) _logger.Info("Ethereum shutdown complete... please wait for all components to close");
        }

        private void Stop(Action stopAction, string description)
        {
            try
            {
                if (_logger.IsInfo) _logger.Info($"{description}...");
                stopAction();
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error($"{description} shutdown error.", e);
            }
        }

        private Task Stop(Func<Task?> stopAction, string description)
        {
            try
            {
                if (_logger.IsInfo) _logger.Info($"{description}...");
                return stopAction() ?? Task.CompletedTask;
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error($"{description} shutdown error.", e);
                return Task.CompletedTask;
            }
        }
    }
}
