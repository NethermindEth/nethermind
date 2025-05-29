// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle.Json;

namespace Nethermind.Specs.ChainSpecStyle
{
    public class ChainSpecBasedSpecProvider : SpecProviderBase, ISpecProvider
    {
        private readonly ChainSpec _chainSpec;

        public ChainSpecBasedSpecProvider(ChainSpec chainSpec, ILogManager logManager = null)
            : base(logManager?.GetClassLogger<ChainSpecBasedSpecProvider>() ?? LimboTraceLogger.Instance)
        {
            _chainSpec = chainSpec ?? throw new ArgumentNullException(nameof(chainSpec));
            BuildTransitions();
        }

        public bool GenesisStateUnavailable { get => _chainSpec.GenesisStateUnavailable; }

        protected virtual ReleaseSpec CreateEmptyReleaseSpec() => new();

        private void BuildTransitions()
        {
            SortedSet<long> transitionBlockNumbers = new();
            SortedSet<ulong> transitionTimestamps = new();
            transitionBlockNumbers.Add(0L);

            foreach (IChainSpecEngineParameters item in _chainSpec.EngineChainSpecParametersProvider
                         .AllChainSpecParameters)
            {
                item.AddTransitions(transitionBlockNumbers, transitionTimestamps);
            }

            AddTransitions(transitionBlockNumbers, _chainSpec, static n => n.EndsWith("BlockNumber") && n != "TerminalPoWBlockNumber");
            AddTransitions(transitionBlockNumbers, _chainSpec.Parameters, static n => n.EndsWith("Transition"));
            AddTransitions(transitionTimestamps, _chainSpec.Parameters, static n => n.EndsWith("TransitionTimestamp"), _chainSpec.Genesis?.Timestamp ?? 0);
            AddBlobScheduleTransitions(transitionTimestamps, _chainSpec);
            TimestampFork = transitionTimestamps.Count > 0 ? transitionTimestamps.Min : ISpecProvider.TimestampForkNever;

            static void AddTransitions<T>(
                SortedSet<T> transitions,
                object value,
                Func<string, bool> matchPropertyName, T? minValueExclusive = null)
                where T : struct, INumber<T>
            {
                static void Add(SortedSet<T> transitions, T value, T? minValueExclusive)
                {
                    if (minValueExclusive is null || value > minValueExclusive)
                    {
                        transitions.Add(value);
                    }
                }

                if (value is not null)
                {
                    IEnumerable<PropertyInfo> properties = value.GetType()
                        .GetProperties(BindingFlags.Public | BindingFlags.Instance);

                    foreach (PropertyInfo propertyInfo in properties)
                    {
                        if (matchPropertyName(propertyInfo.Name))
                        {
                            if (propertyInfo.PropertyType == typeof(T))
                            {
                                Add(transitions, (T)propertyInfo.GetValue(value)!, minValueExclusive);
                            }
                            else if (propertyInfo.PropertyType == typeof(T?))
                            {
                                T? optionalTransition = (T?)propertyInfo.GetValue(value);
                                if (optionalTransition is not null)
                                {
                                    Add(transitions, optionalTransition.Value, minValueExclusive);
                                }
                            }
                        }
                    }
                }
            }

            static void AddBlobScheduleTransitions(SortedSet<ulong> transitions, ChainSpec chainSpec)
            {
                if (chainSpec.Parameters.BlobSchedule is not { Count: > 0 })
                {
                    return;
                }

                ulong genesisTimestamp = chainSpec.Genesis?.Timestamp ?? 0;
                ulong eip4844Timestamp = chainSpec.Parameters.Eip4844TransitionTimestamp
                    ?? throw new ArgumentException($"{nameof(chainSpec.Parameters.Eip4844TransitionTimestamp)} should be set in order to use {nameof(_chainSpec.Parameters.BlobSchedule)}");

                foreach (BlobScheduleSettings settings in chainSpec.Parameters.BlobSchedule)
                {
                    if (settings.Timestamp == genesisTimestamp)
                    {
                        continue;
                    }

                    if (settings.Timestamp < eip4844Timestamp)
                    {
                        throw new ArgumentException($"Blob settings are scheduled at {settings.Timestamp}, before EIP-4844, activated at {chainSpec.Parameters.Eip4844TransitionTimestamp}");
                    }

                    if (settings.Target > settings.Max)
                    {
                        throw new ArgumentException($"Blob schedule target ({settings.Target}) should not exceed max ({settings.Max}).");
                    }

                    transitions.Add(settings.Timestamp);
                }
            }

            (ForkActivation Activation, IReleaseSpec Spec)[] allTransitions = CreateTransitions(_chainSpec, transitionBlockNumbers, transitionTimestamps);

            LoadTransitions(allTransitions);

            TransitionActivations = CreateTransitionActivations(transitionBlockNumbers, transitionTimestamps);

            if (_chainSpec.Parameters.TerminalPoWBlockNumber is not null)
            {
                MergeBlockNumber = (ForkActivation)(_chainSpec.Parameters.TerminalPoWBlockNumber + 1);
            }

            TerminalTotalDifficulty = _chainSpec.Parameters.TerminalTotalDifficulty;
        }

        private (ForkActivation, IReleaseSpec Spec)[] CreateTransitions(
            ChainSpec chainSpec,
            SortedSet<long> transitionBlockNumbers,
            SortedSet<ulong> transitionTimestamps)
        {
            (ForkActivation Activation, IReleaseSpec Spec)[] transitions = new (ForkActivation, IReleaseSpec Spec)[transitionBlockNumbers.Count + transitionTimestamps.Count];
            long biggestBlockTransition = transitionBlockNumbers.Max;

            int index = 0;
            foreach (long releaseStartBlock in transitionBlockNumbers)
            {
                IReleaseSpec releaseSpec = CreateReleaseSpec(chainSpec, releaseStartBlock, chainSpec.Genesis?.Timestamp ?? 0);
                transitions[index++] = ((ForkActivation)releaseStartBlock, releaseSpec);
            }

            foreach (ulong releaseStartTimestamp in transitionTimestamps)
            {
                long activationBlockNumber = biggestBlockTransition;
                ForkActivation forkActivation = (activationBlockNumber, releaseStartTimestamp);
                IReleaseSpec releaseSpec = CreateReleaseSpec(chainSpec, activationBlockNumber, releaseStartTimestamp);
                transitions[index++] = (forkActivation, releaseSpec);
            }

            return transitions;
        }

        private static ForkActivation[] CreateTransitionActivations(SortedSet<long> transitionBlockNumbers, SortedSet<ulong> transitionTimestamps)
        {
            long biggestBlockTransition = transitionBlockNumbers.Max;

            ForkActivation[] transitionActivations = new ForkActivation[transitionBlockNumbers.Count - 1 + transitionTimestamps.Count];

            int index = 0;
            foreach (long blockNumber in transitionBlockNumbers.Skip(1))
            {
                transitionActivations[index++] = new ForkActivation(blockNumber);
            }

            foreach (ulong timestamp in transitionTimestamps)
            {
                transitionActivations[index++] = new ForkActivation(biggestBlockTransition, timestamp);
            }

            return transitionActivations;
        }

        protected virtual ReleaseSpec CreateReleaseSpec(ChainSpec chainSpec, long releaseStartBlock, ulong? releaseStartTimestamp = null)
        {
            ReleaseSpec releaseSpec = CreateEmptyReleaseSpec();
            releaseSpec.MaximumUncleCount = 2;
            releaseSpec.DifficultyBoundDivisor = 1;
            releaseSpec.IsTimeAdjustmentPostOlympic = true; // TODO: this is Duration, review
            releaseSpec.MaximumExtraDataSize = chainSpec.Parameters.MaximumExtraDataSize;
            releaseSpec.MinGasLimit = chainSpec.Parameters.MinGasLimit;
            releaseSpec.GasLimitBoundDivisor = chainSpec.Parameters.GasLimitBoundDivisor;
            releaseSpec.IsEip170Enabled = (chainSpec.Parameters.MaxCodeSizeTransition ?? long.MaxValue) <= releaseStartBlock ||
                                          (chainSpec.Parameters.MaxCodeSizeTransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.MaxCodeSize = releaseSpec.IsEip170Enabled ? (chainSpec.Parameters.MaxCodeSize ?? long.MaxValue) : long.MaxValue;
            releaseSpec.IsEip2Enabled = true;
            releaseSpec.IsEip100Enabled = true;
            releaseSpec.IsEip7Enabled = (chainSpec.Parameters.Eip7Transition ?? 0) <= releaseStartBlock;
            releaseSpec.IsEip140Enabled = (chainSpec.Parameters.Eip140Transition ?? 0) <= releaseStartBlock;
            releaseSpec.IsEip145Enabled = (chainSpec.Parameters.Eip145Transition ?? 0) <= releaseStartBlock;
            releaseSpec.IsEip150Enabled = (chainSpec.Parameters.Eip150Transition ?? 0) <= releaseStartBlock;
            releaseSpec.IsEip152Enabled = (chainSpec.Parameters.Eip152Transition ?? long.MaxValue) <= releaseStartBlock;
            releaseSpec.IsEip155Enabled = (chainSpec.Parameters.Eip155Transition ?? 0) <= releaseStartBlock;
            releaseSpec.IsEip160Enabled = (chainSpec.Parameters.Eip160Transition ?? 0) <= releaseStartBlock;
            releaseSpec.IsEip158Enabled = (chainSpec.Parameters.Eip161abcTransition ?? 0) <= releaseStartBlock;
            releaseSpec.IsEip196Enabled = (chainSpec.ByzantiumBlockNumber ?? 0) <= releaseStartBlock;
            releaseSpec.IsEip197Enabled = (chainSpec.ByzantiumBlockNumber ?? 0) <= releaseStartBlock;
            releaseSpec.IsEip198Enabled = (chainSpec.ByzantiumBlockNumber ?? 0) <= releaseStartBlock;
            releaseSpec.IsEip211Enabled = (chainSpec.Parameters.Eip211Transition ?? 0) <= releaseStartBlock;
            releaseSpec.IsEip214Enabled = (chainSpec.Parameters.Eip214Transition ?? 0) <= releaseStartBlock;
            releaseSpec.IsEip658Enabled = (chainSpec.Parameters.Eip658Transition ?? 0) <= releaseStartBlock;
            releaseSpec.IsEip649Enabled = (chainSpec.ByzantiumBlockNumber ?? 0) <= releaseStartBlock;
            releaseSpec.IsEip1014Enabled = (chainSpec.Parameters.Eip1014Transition ?? 0) <= releaseStartBlock;
            releaseSpec.IsEip1052Enabled = (chainSpec.Parameters.Eip1052Transition ?? 0) <= releaseStartBlock;
            releaseSpec.IsEip1108Enabled = (chainSpec.Parameters.Eip1108Transition ?? long.MaxValue) <= releaseStartBlock;
            releaseSpec.IsEip1234Enabled = (chainSpec.ConstantinopleBlockNumber ?? chainSpec.ConstantinopleFixBlockNumber ?? 0) <= releaseStartBlock;
            releaseSpec.IsEip1283Enabled = (chainSpec.Parameters.Eip1283Transition ?? long.MaxValue) <= releaseStartBlock && ((chainSpec.Parameters.Eip1283DisableTransition ?? long.MaxValue) > releaseStartBlock || (chainSpec.Parameters.Eip1283ReenableTransition ?? long.MaxValue) <= releaseStartBlock);
            releaseSpec.IsEip1344Enabled = (chainSpec.Parameters.Eip1344Transition ?? long.MaxValue) <= releaseStartBlock;
            releaseSpec.IsEip1884Enabled = (chainSpec.Parameters.Eip1884Transition ?? long.MaxValue) <= releaseStartBlock;
            releaseSpec.IsEip2028Enabled = (chainSpec.Parameters.Eip2028Transition ?? long.MaxValue) <= releaseStartBlock;
            releaseSpec.IsEip2200Enabled = (chainSpec.Parameters.Eip2200Transition ?? long.MaxValue) <= releaseStartBlock || (chainSpec.Parameters.Eip1706Transition ?? long.MaxValue) <= releaseStartBlock && releaseSpec.IsEip1283Enabled;
            releaseSpec.IsEip1559Enabled = (chainSpec.Parameters.Eip1559Transition ?? long.MaxValue) <= releaseStartBlock;
            releaseSpec.Eip1559TransitionBlock = chainSpec.Parameters.Eip1559Transition ?? long.MaxValue;
            releaseSpec.IsEip2537Enabled = (chainSpec.Parameters.Eip2537Transition ?? long.MaxValue) <= releaseStartBlock ||
                                           (chainSpec.Parameters.Eip2537TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.IsEip2565Enabled = (chainSpec.Parameters.Eip2565Transition ?? long.MaxValue) <= releaseStartBlock;
            releaseSpec.IsEip2929Enabled = (chainSpec.Parameters.Eip2929Transition ?? long.MaxValue) <= releaseStartBlock;
            releaseSpec.IsEip2930Enabled = (chainSpec.Parameters.Eip2930Transition ?? long.MaxValue) <= releaseStartBlock;
            releaseSpec.IsEip3198Enabled = (chainSpec.Parameters.Eip3198Transition ?? long.MaxValue) <= releaseStartBlock;
            releaseSpec.IsEip3541Enabled = (chainSpec.Parameters.Eip3541Transition ?? long.MaxValue) <= releaseStartBlock;
            releaseSpec.IsEip3529Enabled = (chainSpec.Parameters.Eip3529Transition ?? long.MaxValue) <= releaseStartBlock;
            releaseSpec.IsEip3607Enabled = (chainSpec.Parameters.Eip3607Transition ?? long.MaxValue) <= releaseStartBlock;
            releaseSpec.ValidateChainId = (chainSpec.Parameters.ValidateChainIdTransition ?? 0) <= releaseStartBlock;
            releaseSpec.ValidateReceipts = ((chainSpec.Parameters.ValidateReceiptsTransition > 0) ? Math.Max(chainSpec.Parameters.ValidateReceiptsTransition ?? 0, chainSpec.Parameters.Eip658Transition ?? 0) : 0) <= releaseStartBlock;
            releaseSpec.Eip1559BaseFeeMinValue = releaseSpec.IsEip1559Enabled && (chainSpec.Parameters.Eip1559BaseFeeMinValueTransition ?? long.MaxValue) <= releaseStartBlock ? chainSpec.Parameters.Eip1559BaseFeeMinValue : null;
            releaseSpec.ElasticityMultiplier = chainSpec.Parameters.Eip1559ElasticityMultiplier ?? Eip1559Constants.DefaultElasticityMultiplier;
            releaseSpec.ForkBaseFee = chainSpec.Parameters.Eip1559BaseFeeInitialValue ?? Eip1559Constants.DefaultForkBaseFee;
            releaseSpec.BaseFeeMaxChangeDenominator = chainSpec.Parameters.Eip1559BaseFeeMaxChangeDenominator ?? Eip1559Constants.DefaultBaseFeeMaxChangeDenominator;

            releaseSpec.IsEip1153Enabled = (chainSpec.Parameters.Eip1153TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.IsEip3651Enabled = (chainSpec.Parameters.Eip3651TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.IsEip3855Enabled = (chainSpec.Parameters.Eip3855TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.IsEip3860Enabled = (chainSpec.Parameters.Eip3860TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.IsEip4895Enabled = (chainSpec.Parameters.Eip4895TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.WithdrawalTimestamp = chainSpec.Parameters.Eip4895TransitionTimestamp ?? ulong.MaxValue;

            releaseSpec.IsEip4844Enabled = (chainSpec.Parameters.Eip4844TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.IsRip7212Enabled = (chainSpec.Parameters.Rip7212TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.IsOpGraniteEnabled = (chainSpec.Parameters.OpGraniteTransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.IsOpHoloceneEnabled = (chainSpec.Parameters.OpHoloceneTransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.IsOpIsthmusEnabled = (chainSpec.Parameters.OpIsthmusTransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.Eip4844TransitionTimestamp = chainSpec.Parameters.Eip4844TransitionTimestamp ?? ulong.MaxValue;
            releaseSpec.IsEip5656Enabled = (chainSpec.Parameters.Eip5656TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.IsEip6780Enabled = (chainSpec.Parameters.Eip6780TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.IsEip4788Enabled = (chainSpec.Parameters.Eip4788TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.Eip4788ContractAddress = chainSpec.Parameters.Eip4788ContractAddress;
            releaseSpec.IsEip2935Enabled = (chainSpec.Parameters.Eip2935TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.IsEofEnabled = (chainSpec.Parameters.Eip7692TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.Eip2935ContractAddress = chainSpec.Parameters.Eip2935ContractAddress;

            releaseSpec.IsEip7702Enabled = (chainSpec.Parameters.Eip7702TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.IsEip7823Enabled = (chainSpec.Parameters.Eip7823TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;

            releaseSpec.IsEip6110Enabled = (chainSpec.Parameters.Eip6110TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.DepositContractAddress = chainSpec.Parameters.DepositContractAddress;

            releaseSpec.IsEip7002Enabled = (chainSpec.Parameters.Eip7002TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.Eip7002ContractAddress = chainSpec.Parameters.Eip7002ContractAddress;

            releaseSpec.IsEip7251Enabled = (chainSpec.Parameters.Eip7251TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.Eip7251ContractAddress = chainSpec.Parameters.Eip7251ContractAddress;
            releaseSpec.IsEip7623Enabled = (chainSpec.Parameters.Eip7623TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.IsEip7883Enabled = (chainSpec.Parameters.Eip7883TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;

            releaseSpec.IsEip7594Enabled = (chainSpec.Parameters.Eip7594TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.IsEip7825Enabled = (chainSpec.Parameters.Eip7825TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.IsEip7918Enabled = (chainSpec.Parameters.Eip7918TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;

            bool eip1559FeeCollector = releaseSpec.IsEip1559Enabled && (chainSpec.Parameters.Eip1559FeeCollectorTransition ?? long.MaxValue) <= releaseStartBlock;
            bool eip4844FeeCollector = releaseSpec.IsEip4844Enabled && (chainSpec.Parameters.Eip4844FeeCollectorTransitionTimestamp ?? long.MaxValue) <= releaseStartTimestamp;
            releaseSpec.FeeCollector = (eip1559FeeCollector || eip4844FeeCollector) ? chainSpec.Parameters.FeeCollector : null;
            releaseSpec.IsEip4844FeeCollectorEnabled = eip4844FeeCollector;

            foreach (IChainSpecEngineParameters item in _chainSpec.EngineChainSpecParametersProvider
                         .AllChainSpecParameters)
            {
                item.ApplyToReleaseSpec(releaseSpec, releaseStartBlock, releaseStartTimestamp);
            }

            SetBlobScheduleParameters();

            return releaseSpec;

            void SetBlobScheduleParameters()
            {
                if (releaseSpec.Eip4844TransitionTimestamp > releaseStartTimestamp)
                {
                    return;
                }

                BlobScheduleSettings? blobSchedule = chainSpec.Parameters.BlobSchedule.OrderByDescending(bs => bs).FirstOrDefault(bs => bs.Timestamp <= releaseStartTimestamp);

                if (blobSchedule is not null)
                {
                    releaseSpec.TargetBlobCount = blobSchedule.Target;
                    releaseSpec.MaxBlobCount = blobSchedule.Max;
                    releaseSpec.BlobBaseFeeUpdateFraction = blobSchedule.BaseFeeUpdateFraction;
                }
                else if (releaseSpec.Eip4844TransitionTimestamp <= releaseStartTimestamp)
                {
                    releaseSpec.TargetBlobCount = Eip4844Constants.DefaultTargetBlobCount;
                    releaseSpec.MaxBlobCount = Eip4844Constants.DefaultMaxBlobCount;
                    releaseSpec.BlobBaseFeeUpdateFraction = Eip4844Constants.DefaultBlobGasPriceUpdateFraction;
                }
            }
        }

        public void UpdateMergeTransitionInfo(long? blockNumber, UInt256? terminalTotalDifficulty = null)
        {
            if (blockNumber is not null)
            {
                MergeBlockNumber = (ForkActivation)blockNumber;
            }

            if (terminalTotalDifficulty is not null)
            {
                TerminalTotalDifficulty = terminalTotalDifficulty;
            }
        }

        public ForkActivation? MergeBlockNumber { get; private set; }
        public ulong TimestampFork { get; private set; }

        public UInt256? TerminalTotalDifficulty { get; private set; }

        public long? DaoBlockNumber => _chainSpec.DaoForkBlockNumber;
        public ulong? BeaconChainGenesisTimestamp => _chainSpec.Parameters.BeaconChainGenesisTimestamp;

        public ulong NetworkId => _chainSpec.NetworkId;
        public ulong ChainId => _chainSpec.ChainId;
        public string SealEngine => _chainSpec.SealEngineType;
    }
}
