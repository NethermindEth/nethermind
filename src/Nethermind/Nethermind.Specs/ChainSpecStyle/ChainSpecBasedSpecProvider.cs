// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Nethermind.Core.Collections;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.Specs.ChainSpecStyle
{
    public class ChainSpecBasedSpecProvider : ISpecProvider
    {
        private (ForkActivation Activation, ReleaseSpec Spec)[] _transitions;
        private ForkActivation? _firstTimestampActivation;

        private readonly ChainSpec _chainSpec;
        private readonly ILogger _logger;

        public ChainSpecBasedSpecProvider(ChainSpec chainSpec, ILogManager logManager = null)
        {
            _chainSpec = chainSpec ?? throw new ArgumentNullException(nameof(chainSpec));
            _logger = logManager?.GetClassLogger<ChainSpecBasedSpecProvider>() ?? LimboTraceLogger.Instance;
            BuildTransitions();
        }

        private void BuildTransitions()
        {
            SortedSet<long> transitionBlockNumbers = new();
            SortedSet<ulong> transitionTimestamps = new();
            transitionBlockNumbers.Add(0L);

            if (_chainSpec.Ethash?.BlockRewards is not null)
            {
                foreach ((long blockNumber, _) in _chainSpec.Ethash.BlockRewards)
                {
                    transitionBlockNumbers.Add(blockNumber);
                }
            }

            AddTransitions(transitionBlockNumbers, _chainSpec, n => n.EndsWith("BlockNumber") && n != "TerminalPoWBlockNumber");
            AddTransitions(transitionBlockNumbers, _chainSpec.Parameters, n => n.EndsWith("Transition"));
            AddTransitions(transitionBlockNumbers, _chainSpec.Ethash, n => n.EndsWith("Transition"));
            AddTransitions(transitionTimestamps, _chainSpec.Parameters, n => n.EndsWith("TransitionTimestamp"), _chainSpec.Genesis?.Timestamp ?? 0);
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

            foreach (KeyValuePair<long, long> bombDelay in _chainSpec.Ethash?.DifficultyBombDelays ?? Enumerable.Empty<KeyValuePair<long, long>>())
            {
                transitionBlockNumbers.Add(bombDelay.Key);
            }

            TransitionActivations = CreateTransitionActivations(transitionBlockNumbers, transitionTimestamps);
            _transitions = CreateTransitions(_chainSpec, transitionBlockNumbers, transitionTimestamps);
            _firstTimestampActivation = TransitionActivations.FirstOrDefault(t => t.Timestamp is not null);

            if (_chainSpec.Parameters.TerminalPoWBlockNumber is not null)
            {
                MergeBlockNumber = (ForkActivation)(_chainSpec.Parameters.TerminalPoWBlockNumber + 1);
            }

            TerminalTotalDifficulty = _chainSpec.Parameters.TerminalTotalDifficulty;
        }

        private static (ForkActivation, ReleaseSpec Spec)[] CreateTransitions(
            ChainSpec chainSpec,
            SortedSet<long> transitionBlockNumbers,
            SortedSet<ulong> transitionTimestamps)
        {
            (ForkActivation Activation, ReleaseSpec Spec)[] transitions = new (ForkActivation, ReleaseSpec Spec)[transitionBlockNumbers.Count + transitionTimestamps.Count];
            long biggestBlockTransition = transitionBlockNumbers.Max;

            int index = 0;
            foreach (long releaseStartBlock in transitionBlockNumbers)
            {
                ReleaseSpec releaseSpec = CreateReleaseSpec(chainSpec, releaseStartBlock, chainSpec.Genesis?.Timestamp ?? 0);
                transitions[index++] = ((ForkActivation)releaseStartBlock, releaseSpec);
            }

            foreach (ulong releaseStartTimestamp in transitionTimestamps)
            {
                long activationBlockNumber = biggestBlockTransition;
                ForkActivation forkActivation = (activationBlockNumber, releaseStartTimestamp);
                ReleaseSpec releaseSpec = CreateReleaseSpec(chainSpec, activationBlockNumber, releaseStartTimestamp);
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

        private static ReleaseSpec CreateReleaseSpec(ChainSpec chainSpec, long releaseStartBlock, ulong? releaseStartTimestamp = null)
        {
            ReleaseSpec releaseSpec = new();
            releaseSpec.MaximumUncleCount = (int)(releaseStartBlock >= (chainSpec.AuRa?.MaximumUncleCountTransition ?? long.MaxValue) ? chainSpec.AuRa?.MaximumUncleCount ?? 2 : 2);
            releaseSpec.IsTimeAdjustmentPostOlympic = true; // TODO: this is Duration, review
            releaseSpec.MaximumExtraDataSize = chainSpec.Parameters.MaximumExtraDataSize;
            releaseSpec.MinGasLimit = chainSpec.Parameters.MinGasLimit;
            releaseSpec.GasLimitBoundDivisor = chainSpec.Parameters.GasLimitBoundDivisor;
            releaseSpec.DifficultyBoundDivisor = chainSpec.Ethash?.DifficultyBoundDivisor ?? 1;
            releaseSpec.FixedDifficulty = chainSpec.Ethash?.FixedDifficulty;
            releaseSpec.IsEip170Enabled = (chainSpec.Parameters.MaxCodeSizeTransition ?? long.MaxValue) <= releaseStartBlock ||
                                          (chainSpec.Parameters.MaxCodeSizeTransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.MaxCodeSize = releaseSpec.IsEip170Enabled ? (chainSpec.Parameters.MaxCodeSize ?? long.MaxValue) : long.MaxValue;
            releaseSpec.IsEip2Enabled = (chainSpec.Ethash?.HomesteadTransition ?? 0) <= releaseStartBlock;
            releaseSpec.IsEip7Enabled = (chainSpec.Ethash?.HomesteadTransition ?? 0) <= releaseStartBlock ||
                                        (chainSpec.Parameters.Eip7Transition ?? long.MaxValue) <= releaseStartBlock;
            releaseSpec.IsEip100Enabled = (chainSpec.Ethash?.Eip100bTransition ?? 0) <= releaseStartBlock;
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
            releaseSpec.IsEip2315Enabled = (chainSpec.Parameters.Eip2315Transition ?? long.MaxValue) <= releaseStartBlock;
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
            releaseSpec.Eip1559FeeCollector = releaseSpec.IsEip1559Enabled && (chainSpec.Parameters.Eip1559FeeCollectorTransition ?? long.MaxValue) <= releaseStartBlock ? chainSpec.Parameters.Eip1559FeeCollector : null;
            releaseSpec.Eip1559BaseFeeMinValue = releaseSpec.IsEip1559Enabled && (chainSpec.Parameters.Eip1559BaseFeeMinValueTransition ?? long.MaxValue) <= releaseStartBlock ? chainSpec.Parameters.Eip1559BaseFeeMinValue : null;

            if (chainSpec.Ethash is not null)
            {
                foreach (KeyValuePair<long, UInt256> blockReward in chainSpec.Ethash.BlockRewards ?? Enumerable.Empty<KeyValuePair<long, UInt256>>())
                {
                    if (blockReward.Key <= releaseStartBlock)
                    {
                        releaseSpec.BlockReward = blockReward.Value;
                    }
                }

                foreach (KeyValuePair<long, long> bombDelay in chainSpec.Ethash.DifficultyBombDelays ?? Enumerable.Empty<KeyValuePair<long, long>>())
                {
                    if (bombDelay.Key <= releaseStartBlock)
                    {
                        releaseSpec.DifficultyBombDelay += bombDelay.Value;
                    }
                }
            }

            releaseSpec.IsEip1153Enabled = (chainSpec.Parameters.Eip1153TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.IsEip3651Enabled = (chainSpec.Parameters.Eip3651TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.IsEip3855Enabled = (chainSpec.Parameters.Eip3855TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.IsEip3860Enabled = (chainSpec.Parameters.Eip3860TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.IsEip1153Enabled = (chainSpec.Parameters.Eip1153TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.IsEip3651Enabled = (chainSpec.Parameters.Eip3651TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.IsEip3540Enabled = (chainSpec.Parameters.Eip3540TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.IsEip3670Enabled = (chainSpec.Parameters.Eip3670TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.IsEip4200Enabled = (chainSpec.Parameters.Eip4200TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.IsEip4750Enabled = (chainSpec.Parameters.Eip4750TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.IsEip5450Enabled = (chainSpec.Parameters.Eip5450TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.IsEip4895Enabled = (chainSpec.Parameters.Eip4895TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.WithdrawalTimestamp = chainSpec.Parameters.Eip4895TransitionTimestamp ?? ulong.MaxValue;

            releaseSpec.IsEip4844Enabled = (chainSpec.Parameters.Eip4844TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.Eip4844TransitionTimestamp = chainSpec.Parameters.Eip4844TransitionTimestamp ?? ulong.MaxValue;

            return releaseSpec;
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

        public IReleaseSpec GenesisSpec => _transitions.Length == 0 ? null : _transitions[0].Spec;

        public IReleaseSpec GetSpec(ForkActivation activation)
        {
            // TODO: Is this actually needed? Can this be tricked with invalid activation check if someone would fake timestamp from the future?
            if (_firstTimestampActivation is not null && activation.Timestamp is not null)
            {
                if (_firstTimestampActivation.Value.Timestamp < activation.Timestamp
                    && _firstTimestampActivation.Value.BlockNumber > activation.BlockNumber)
                {
                    if (_logger.IsWarn) _logger.Warn($"Chainspec file is misconfigured! Timestamp transition is configured to happen before the last block transition.");
                }
            }

            return _transitions.TryGetSearchedItem(activation,
                CompareTransitionOnActivation,
                out (ForkActivation Activation, ReleaseSpec Spec) transition)
                ? transition.Spec
                : GenesisSpec;
        }

        private static int CompareTransitionOnActivation(ForkActivation activation, (ForkActivation Activation, ReleaseSpec Spec) transition) =>
            activation.CompareTo(transition.Activation);

        public long? DaoBlockNumber => _chainSpec.DaoForkBlockNumber;

        public ulong NetworkId => _chainSpec.NetworkId;
        public ulong ChainId => _chainSpec.ChainId;
        public ForkActivation[] TransitionActivations { get; private set; }
    }
}
