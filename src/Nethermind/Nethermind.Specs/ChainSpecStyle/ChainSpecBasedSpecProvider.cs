// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Nethermind.Core.Collections;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.Specs.ChainSpecStyle
{
    public class ChainSpecBasedSpecProvider : ISpecProvider
    {
        private (ForkActivation Activation, ReleaseSpec Release)[] _transitions;

        private ChainSpec _chainSpec;
        private long _biggestBlockTransition;
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

            var blockNumberforks = _chainSpec.GetType()
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.Name.EndsWith("BlockNumber") && p.Name != "TerminalPoWBlockNumber");

            var timestampForks = _chainSpec.GetType()
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.Name.EndsWith("Timestamp"));

            var baseTransitions = _chainSpec.Parameters.GetType()
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.Name.EndsWith("Transition"));

            var ethashTransitions = _chainSpec.Ethash?.GetType()
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.Name.EndsWith("Transition")) ?? Enumerable.Empty<PropertyInfo>();

            var timestampBaseTransitions = _chainSpec.Parameters.GetType()
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.Name.EndsWith("TransitionTimestamp"));

            var blockNumberTransitionProperties =
                blockNumberforks.Union(baseTransitions.Union(ethashTransitions));

            foreach (PropertyInfo propertyInfo in blockNumberTransitionProperties)
            {
                if (propertyInfo.PropertyType == typeof(long))
                {
                    transitionBlockNumbers.Add((long)propertyInfo.GetValue(propertyInfo.DeclaringType == typeof(ChainSpec) ? _chainSpec : propertyInfo.DeclaringType == typeof(EthashParameters) ? (object)_chainSpec.Ethash : _chainSpec.Parameters));
                }
                else if (propertyInfo.PropertyType == typeof(long?))
                {
                    var optionalTransition = (long?)propertyInfo.GetValue(propertyInfo.DeclaringType == typeof(ChainSpec) ? _chainSpec : propertyInfo.DeclaringType == typeof(EthashParameters) ? (object)_chainSpec.Ethash : _chainSpec.Parameters);
                    if (optionalTransition is not null)
                    {
                        transitionBlockNumbers.Add(optionalTransition.Value);
                    }
                }
            }

            foreach (PropertyInfo propertyInfo in timestampBaseTransitions)
            {
                if (propertyInfo.PropertyType == typeof(ulong))
                {
                    ulong timestampTansition = (ulong)propertyInfo.GetValue(propertyInfo.DeclaringType == typeof(ChainSpec) ? _chainSpec : propertyInfo.DeclaringType == typeof(EthashParameters) ? (object)_chainSpec.Ethash : _chainSpec.Parameters);
                    if (timestampTansition > (_chainSpec?.Genesis?.Timestamp ?? 0))
                        transitionTimestamps.Add(timestampTansition);
                }
                else if (propertyInfo.PropertyType == typeof(ulong?))
                {
                    var optionalTransition = (ulong?)propertyInfo.GetValue(propertyInfo.DeclaringType == typeof(ChainSpec) ? _chainSpec : propertyInfo.DeclaringType == typeof(EthashParameters) ? (object)_chainSpec.Ethash : _chainSpec.Parameters);
                    if (optionalTransition is not null && optionalTransition.Value > (_chainSpec?.Genesis?.Timestamp ?? 0))
                    {
                        transitionTimestamps.Add(optionalTransition.Value);
                    }
                }
            }


            foreach (KeyValuePair<long, long> bombDelay in _chainSpec.Ethash?.DifficultyBombDelays ?? Enumerable.Empty<KeyValuePair<long, long>>())
            {
                transitionBlockNumbers.Add(bombDelay.Key);
            }
            _biggestBlockTransition = transitionBlockNumbers.Last();
            TransitionActivations = transitionBlockNumbers.Skip(1).Select(bn => new ForkActivation(bn))
                .Union(
                transitionTimestamps.Select(ts => new ForkActivation(_biggestBlockTransition, ts))
                )
                .ToArray();
            _transitions = new (ForkActivation, ReleaseSpec Release)[transitionBlockNumbers.Count + transitionTimestamps.Count];

            int index = 0;
            foreach (long releaseStartBlock in transitionBlockNumbers)
            {
                ReleaseSpec releaseSpec = new();
                FillReleaseSpec(releaseSpec, releaseStartBlock, _chainSpec?.Genesis?.Timestamp ?? 0);
                _transitions[index] = ((ForkActivation)releaseStartBlock, releaseSpec);
                index++;
            }

            foreach (ulong releaseStartTimestamp in transitionTimestamps)
            {
                ReleaseSpec releaseSpec = new();
                FillReleaseSpec(releaseSpec, _transitions[index - 1].Activation.BlockNumber, releaseStartTimestamp);
                _transitions[index] = ((_transitions[index - 1].Activation.BlockNumber, releaseStartTimestamp), releaseSpec);
                index++;
            }
            if (_chainSpec.Parameters.TerminalPowBlockNumber is not null)
                MergeBlockNumber = (ForkActivation)(_chainSpec.Parameters.TerminalPowBlockNumber + 1);
            TerminalTotalDifficulty = _chainSpec.Parameters.TerminalTotalDifficulty;
        }

        private void FillReleaseSpec(ReleaseSpec releaseSpec, long releaseStartBlock, ulong? releaseStartTimestamp = null)
        {
            releaseSpec.MaximumUncleCount = (int)(releaseStartBlock >= (_chainSpec.AuRa?.MaximumUncleCountTransition ?? long.MaxValue) ? _chainSpec.AuRa?.MaximumUncleCount ?? 2 : 2);
            releaseSpec.IsTimeAdjustmentPostOlympic = true; // TODO: this is Duration, review
            releaseSpec.MaximumExtraDataSize = _chainSpec.Parameters.MaximumExtraDataSize;
            releaseSpec.MinGasLimit = _chainSpec.Parameters.MinGasLimit;
            releaseSpec.GasLimitBoundDivisor = _chainSpec.Parameters.GasLimitBoundDivisor;
            releaseSpec.DifficultyBoundDivisor = _chainSpec.Ethash?.DifficultyBoundDivisor ?? 1;
            releaseSpec.FixedDifficulty = _chainSpec.Ethash?.FixedDifficulty;
            releaseSpec.MaxCodeSize = _chainSpec.Parameters.MaxCodeSizeTransition > releaseStartBlock ? long.MaxValue : _chainSpec.Parameters.MaxCodeSize;
            releaseSpec.IsEip2Enabled = (_chainSpec.Ethash?.HomesteadTransition ?? 0) <= releaseStartBlock;
            releaseSpec.IsEip7Enabled = (_chainSpec.Ethash?.HomesteadTransition ?? 0) <= releaseStartBlock ||
                                        (_chainSpec.Parameters.Eip7Transition ?? long.MaxValue) <= releaseStartBlock;
            releaseSpec.IsEip100Enabled = (_chainSpec.Ethash?.Eip100bTransition ?? 0) <= releaseStartBlock;
            releaseSpec.IsEip140Enabled = (_chainSpec.Parameters.Eip140Transition ?? 0) <= releaseStartBlock;
            releaseSpec.IsEip145Enabled = (_chainSpec.Parameters.Eip145Transition ?? 0) <= releaseStartBlock;
            releaseSpec.IsEip150Enabled = (_chainSpec.Parameters.Eip150Transition ?? 0) <= releaseStartBlock;
            releaseSpec.IsEip152Enabled = (_chainSpec.Parameters.Eip152Transition ?? long.MaxValue) <= releaseStartBlock;
            releaseSpec.IsEip155Enabled = (_chainSpec.Parameters.Eip155Transition ?? 0) <= releaseStartBlock;
            releaseSpec.IsEip160Enabled = (_chainSpec.Parameters.Eip160Transition ?? 0) <= releaseStartBlock;
            releaseSpec.IsEip158Enabled = (_chainSpec.Parameters.Eip161abcTransition ?? 0) <= releaseStartBlock;
            releaseSpec.IsEip170Enabled = _chainSpec.Parameters.MaxCodeSizeTransition <= releaseStartBlock;
            releaseSpec.IsEip196Enabled = (_chainSpec.ByzantiumBlockNumber ?? 0) <= releaseStartBlock;
            releaseSpec.IsEip197Enabled = (_chainSpec.ByzantiumBlockNumber ?? 0) <= releaseStartBlock;
            releaseSpec.IsEip198Enabled = (_chainSpec.ByzantiumBlockNumber ?? 0) <= releaseStartBlock;
            releaseSpec.IsEip211Enabled = (_chainSpec.Parameters.Eip211Transition ?? 0) <= releaseStartBlock;
            releaseSpec.IsEip214Enabled = (_chainSpec.Parameters.Eip214Transition ?? 0) <= releaseStartBlock;
            releaseSpec.IsEip658Enabled = (_chainSpec.Parameters.Eip658Transition ?? 0) <= releaseStartBlock;
            releaseSpec.IsEip649Enabled = (_chainSpec.ByzantiumBlockNumber ?? 0) <= releaseStartBlock;
            releaseSpec.IsEip1014Enabled = (_chainSpec.Parameters.Eip1014Transition ?? 0) <= releaseStartBlock;
            releaseSpec.IsEip1052Enabled = (_chainSpec.Parameters.Eip1052Transition ?? 0) <= releaseStartBlock;
            releaseSpec.IsEip1108Enabled = (_chainSpec.Parameters.Eip1108Transition ?? long.MaxValue) <= releaseStartBlock;
            releaseSpec.IsEip1234Enabled = (_chainSpec.ConstantinopleBlockNumber ?? _chainSpec.ConstantinopleFixBlockNumber ?? 0) <= releaseStartBlock;
            releaseSpec.IsEip1283Enabled = (_chainSpec.Parameters.Eip1283Transition ?? long.MaxValue) <= releaseStartBlock && ((_chainSpec.Parameters.Eip1283DisableTransition ?? long.MaxValue) > releaseStartBlock || (_chainSpec.Parameters.Eip1283ReenableTransition ?? long.MaxValue) <= releaseStartBlock);
            releaseSpec.IsEip1344Enabled = (_chainSpec.Parameters.Eip1344Transition ?? long.MaxValue) <= releaseStartBlock;
            releaseSpec.IsEip1884Enabled = (_chainSpec.Parameters.Eip1884Transition ?? long.MaxValue) <= releaseStartBlock;
            releaseSpec.IsEip2028Enabled = (_chainSpec.Parameters.Eip2028Transition ?? long.MaxValue) <= releaseStartBlock;
            releaseSpec.IsEip2200Enabled = (_chainSpec.Parameters.Eip2200Transition ?? long.MaxValue) <= releaseStartBlock || (_chainSpec.Parameters.Eip1706Transition ?? long.MaxValue) <= releaseStartBlock && releaseSpec.IsEip1283Enabled;
            releaseSpec.IsEip1559Enabled = (_chainSpec.Parameters.Eip1559Transition ?? long.MaxValue) <= releaseStartBlock;
            releaseSpec.Eip1559TransitionBlock = _chainSpec.Parameters.Eip1559Transition ?? long.MaxValue;
            releaseSpec.IsEip2315Enabled = (_chainSpec.Parameters.Eip2315Transition ?? long.MaxValue) <= releaseStartBlock;
            releaseSpec.IsEip2537Enabled = (_chainSpec.Parameters.Eip2537Transition ?? long.MaxValue) <= releaseStartBlock;
            releaseSpec.IsEip2565Enabled = (_chainSpec.Parameters.Eip2565Transition ?? long.MaxValue) <= releaseStartBlock;
            releaseSpec.IsEip2929Enabled = (_chainSpec.Parameters.Eip2929Transition ?? long.MaxValue) <= releaseStartBlock;
            releaseSpec.IsEip2930Enabled = (_chainSpec.Parameters.Eip2930Transition ?? long.MaxValue) <= releaseStartBlock;
            releaseSpec.IsEip3198Enabled = (_chainSpec.Parameters.Eip3198Transition ?? long.MaxValue) <= releaseStartBlock;
            releaseSpec.IsEip3541Enabled = (_chainSpec.Parameters.Eip3541Transition ?? long.MaxValue) <= releaseStartBlock;
            releaseSpec.IsEip3529Enabled = (_chainSpec.Parameters.Eip3529Transition ?? long.MaxValue) <= releaseStartBlock;
            releaseSpec.IsEip3607Enabled = (_chainSpec.Parameters.Eip3607Transition ?? long.MaxValue) <= releaseStartBlock;
            releaseSpec.ValidateChainId = (_chainSpec.Parameters.ValidateChainIdTransition ?? 0) <= releaseStartBlock;
            releaseSpec.ValidateReceipts = ((_chainSpec.Parameters.ValidateReceiptsTransition > 0) ? Math.Max(_chainSpec.Parameters.ValidateReceiptsTransition ?? 0, _chainSpec.Parameters.Eip658Transition ?? 0) : 0) <= releaseStartBlock;
            releaseSpec.Eip1559FeeCollector = releaseSpec.IsEip1559Enabled && (_chainSpec.Parameters.Eip1559FeeCollectorTransition ?? long.MaxValue) <= releaseStartBlock ? _chainSpec.Parameters.Eip1559FeeCollector : null;
            releaseSpec.Eip1559BaseFeeMinValue = releaseSpec.IsEip1559Enabled && (_chainSpec.Parameters.Eip1559BaseFeeMinValueTransition ?? long.MaxValue) <= releaseStartBlock ? _chainSpec.Parameters.Eip1559BaseFeeMinValue : null;

            if (_chainSpec.Ethash is not null)
            {
                foreach (KeyValuePair<long, UInt256> blockReward in _chainSpec.Ethash.BlockRewards ?? Enumerable.Empty<KeyValuePair<long, UInt256>>())
                {
                    if (blockReward.Key <= releaseStartBlock)
                    {
                        releaseSpec.BlockReward = blockReward.Value;
                    }
                }

                foreach (KeyValuePair<long, long> bombDelay in _chainSpec.Ethash.DifficultyBombDelays ?? Enumerable.Empty<KeyValuePair<long, long>>())
                {
                    if (bombDelay.Key <= releaseStartBlock)
                    {
                        releaseSpec.DifficultyBombDelay += bombDelay.Value;
                    }
                }
            }


            releaseSpec.IsEip1153Enabled = (_chainSpec.Parameters.Eip1153TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.IsEip3651Enabled = (_chainSpec.Parameters.Eip3651TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.IsEip3675Enabled = (_chainSpec.Parameters.Eip3675TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.IsEip3855Enabled = (_chainSpec.Parameters.Eip3855TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.IsEip3860Enabled = (_chainSpec.Parameters.Eip3860TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.IsEip4895Enabled = (_chainSpec.Parameters.Eip4895TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.WithdrawalTimestamp = _chainSpec.Parameters.Eip4895TransitionTimestamp ?? ulong.MaxValue;
        }

        public void UpdateMergeTransitionInfo(long? blockNumber, UInt256? terminalTotalDifficulty = null)
        {
            if (blockNumber is not null)
                MergeBlockNumber = (ForkActivation)blockNumber;
            if (terminalTotalDifficulty is not null)
                TerminalTotalDifficulty = terminalTotalDifficulty;
        }

        public ForkActivation? MergeBlockNumber { get; private set; }

        public UInt256? TerminalTotalDifficulty { get; private set; }

        public IReleaseSpec GenesisSpec => _transitions.Length == 0 ? null : _transitions[0].Release;

        public IReleaseSpec GetSpec(ForkActivation activation)
        {
            if (activation.Timestamp is not null)
            {
                for (int i = 0; i < _transitions.Length; i++)
                {
                    if (_transitions[i].Activation.Timestamp is null)
                        continue;
                    ulong transitionTimestamp = _transitions[i].Activation.Timestamp.Value;
                    if (transitionTimestamp < activation.Timestamp)
                    {
                        if (_transitions[i].Activation.BlockNumber > activation.BlockNumber)
                        {
                            if (_logger.IsWarn) _logger.Warn("Chainspec file is misconfigured! Timestamp transition is configured to happen before the last block transition.");
                        }
                    }
                }
            }
            return _transitions.TryGetSearchedItem(activation,
                CompareTransitionOnBlock,
                out (ForkActivation Activation, ReleaseSpec Release) transition)
                ? transition.Release
                : GenesisSpec;
        }

        private static int CompareTransitionOnBlock(ForkActivation activation, (ForkActivation Activation, ReleaseSpec Release) transition) =>
            activation.CompareTo(transition.Activation);

        public long? DaoBlockNumber => _chainSpec.DaoForkBlockNumber;

        public ulong ChainId => _chainSpec.ChainId;
        public ForkActivation[] TransitionActivations { get; private set; }
    }
}
