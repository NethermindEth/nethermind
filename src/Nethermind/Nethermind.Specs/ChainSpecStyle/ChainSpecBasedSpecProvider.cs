// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
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
    public class ChainSpecBasedSpecProvider : SpecProviderBase, IForkAwareSpecProvider
    {
        private readonly ChainSpec _chainSpec;
        private IForkAwareSpecProvider? _forkAware;

        public ChainSpecBasedSpecProvider(ChainSpec chainSpec, ILogManager? logManager = null)
            : base(logManager?.GetClassLogger<ChainSpecBasedSpecProvider>() ?? LimboTraceLogger.Instance)
        {
            _chainSpec = chainSpec ?? throw new ArgumentNullException(nameof(chainSpec));
            BuildTransitions();
        }

        public bool GenesisStateUnavailable { get => _chainSpec.GenesisStateUnavailable; }

        protected virtual ReleaseSpec CreateEmptyReleaseSpec() => new();

        private void BuildTransitions()
        {
            SortedSet<ulong> transitionBlockNumbers = [];
            SortedSet<ulong> transitionTimestamps = [];
            transitionBlockNumbers.Add(0);

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

            // Scans properties of type T / T? on value whose names pass the filter.
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
                    if (settings.Timestamp <= genesisTimestamp)
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
            _forkAware = ForkAwareForChain(_chainSpec.ChainId);

            if (_chainSpec.Parameters.TerminalPoWBlockNumber is not null)
            {
                MergeBlockNumber = (ForkActivation)(_chainSpec.Parameters.TerminalPoWBlockNumber.Value + 1);
            }

            TerminalTotalDifficulty = _chainSpec.Parameters.TerminalTotalDifficulty;
        }

        private static readonly List<IForkAwareSpecProvider> _knownProviders =
        [
            MainnetSpecProvider.Instance,
            GnosisSpecProvider.Instance,
            ChiadoSpecProvider.Instance,
            SepoliaSpecProvider.Instance,
            HoodiSpecProvider.Instance,
            MordenSpecProvider.Instance,
        ];
        private static FrozenDictionary<ulong, IForkAwareSpecProvider>? _knownProvidersByChainId;

        /// <summary>
        /// Built-in plus plugin-registered <see cref="IForkAwareSpecProvider"/>s, keyed by chain id.
        /// The dictionary is rebuilt lazily after each <see cref="RegisterProvider"/> call.
        /// </summary>
        /// <remarks>Plugin registration is expected at startup only; not safe for concurrent mutation.</remarks>
        public static FrozenDictionary<ulong, IForkAwareSpecProvider> KnownProvidersByChainId =>
            _knownProvidersByChainId ??= _knownProviders.ToFrozenDictionary(static p => p.ChainId);

        /// <summary>
        /// Registers an additional <see cref="IForkAwareSpecProvider"/> (e.g. from a plugin) so that
        /// <see cref="ChainSpecBasedSpecProvider"/> can resolve forks for its chain id. Call at startup.
        /// </summary>
        public static void RegisterProvider(IForkAwareSpecProvider provider)
        {
            _knownProviders.Add(provider);
            _knownProvidersByChainId = null;
        }

        private static IForkAwareSpecProvider? ForkAwareForChain(ulong chainId) =>
            KnownProvidersByChainId.GetValueOrDefault(chainId);

        private (ForkActivation, IReleaseSpec Spec)[] CreateTransitions(
            ChainSpec chainSpec,
            SortedSet<ulong> transitionBlockNumbers,
            SortedSet<ulong> transitionTimestamps)
        {
            (ForkActivation Activation, IReleaseSpec Spec)[] transitions = new (ForkActivation, IReleaseSpec Spec)[transitionBlockNumbers.Count + transitionTimestamps.Count];
            ulong biggestBlockTransition = transitionBlockNumbers.Max;

            int index = 0;
            foreach (ulong releaseStartBlock in transitionBlockNumbers)
            {
                IReleaseSpec releaseSpec = CreateReleaseSpec(chainSpec, releaseStartBlock, chainSpec.Genesis?.Timestamp ?? 0);
                transitions[index++] = ((ForkActivation)releaseStartBlock, releaseSpec);
            }

            foreach (ulong releaseStartTimestamp in transitionTimestamps)
            {
                ForkActivation forkActivation = (biggestBlockTransition, releaseStartTimestamp);
                IReleaseSpec releaseSpec = CreateReleaseSpec(chainSpec, biggestBlockTransition, releaseStartTimestamp);
                transitions[index++] = (forkActivation, releaseSpec);
            }

            return transitions;
        }

        private static ForkActivation[] CreateTransitionActivations(SortedSet<ulong> transitionBlockNumbers, SortedSet<ulong> transitionTimestamps)
        {
            ulong biggestBlockTransition = transitionBlockNumbers.Max;

            ForkActivation[] transitionActivations = new ForkActivation[transitionBlockNumbers.Count - 1 + transitionTimestamps.Count];

            int index = 0;
            foreach (ulong blockNumber in transitionBlockNumbers.Skip(1))
            {
                transitionActivations[index++] = new ForkActivation(blockNumber);
            }

            foreach (ulong timestamp in transitionTimestamps)
            {
                transitionActivations[index++] = new ForkActivation(biggestBlockTransition, timestamp);
            }

            return transitionActivations;
        }

        private static ulong BlockOf(ulong? transition, ulong nullSentinel = ulong.MaxValue) =>
            transition ?? nullSentinel;

        protected virtual ReleaseSpec CreateReleaseSpec(ChainSpec chainSpec, ulong releaseStartBlock, ulong? releaseStartTimestamp = null)
        {
            ulong block = releaseStartBlock;
            ReleaseSpec releaseSpec = CreateEmptyReleaseSpec();
            releaseSpec.MaximumUncleCount = IsPostMergeRelease(chainSpec, releaseStartBlock, releaseStartTimestamp) ? 0 : 2;
            releaseSpec.DifficultyBoundDivisor = 1;
            releaseSpec.IsTimeAdjustmentPostOlympic = true; // TODO: this is Duration, review
            releaseSpec.MaximumExtraDataSize = chainSpec.Parameters.MaximumExtraDataSize;
            releaseSpec.MinGasLimit = chainSpec.Parameters.MinGasLimit;
            releaseSpec.MinHistoryRetentionEpochs = chainSpec.Parameters.MinHistoryRetentionEpochs;
            releaseSpec.MinBalRetentionEpochs = chainSpec.Parameters.MinBalRetentionEpochs;
            releaseSpec.GasLimitBoundDivisor = chainSpec.Parameters.GasLimitBoundDivisor;
            releaseSpec.IsEip170Enabled = BlockOf(chainSpec.Parameters.MaxCodeSizeTransition) <= block ||
                                          (chainSpec.Parameters.MaxCodeSizeTransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.MaxCodeSize = releaseSpec.IsEip170Enabled ? (chainSpec.Parameters.MaxCodeSize ?? long.MaxValue) : long.MaxValue;
            releaseSpec.IsEip2Enabled = true;
            releaseSpec.IsEip100Enabled = true;
            releaseSpec.IsEip7Enabled = BlockOf(chainSpec.Parameters.Eip7Transition, 0) <= block;
            releaseSpec.IsEip140Enabled = BlockOf(chainSpec.Parameters.Eip140Transition, 0) <= block;
            releaseSpec.IsEip145Enabled = BlockOf(chainSpec.Parameters.Eip145Transition, 0) <= block;
            releaseSpec.IsEip150Enabled = BlockOf(chainSpec.Parameters.Eip150Transition, 0) <= block;
            releaseSpec.IsEip152Enabled = BlockOf(chainSpec.Parameters.Eip152Transition) <= block;
            releaseSpec.IsEip155Enabled = BlockOf(chainSpec.Parameters.Eip155Transition, 0) <= block;
            releaseSpec.IsEip160Enabled = BlockOf(chainSpec.Parameters.Eip160Transition, 0) <= block;
            releaseSpec.IsEip158Enabled = BlockOf(chainSpec.Parameters.Eip161abcTransition, 0) <= block;
            releaseSpec.IsEip196Enabled = (chainSpec.ByzantiumBlockNumber ?? 0UL) <= block;
            releaseSpec.IsEip197Enabled = (chainSpec.ByzantiumBlockNumber ?? 0UL) <= block;
            releaseSpec.IsEip198Enabled = (chainSpec.ByzantiumBlockNumber ?? 0UL) <= block;
            releaseSpec.IsEip211Enabled = BlockOf(chainSpec.Parameters.Eip211Transition, 0) <= block;
            releaseSpec.IsEip214Enabled = BlockOf(chainSpec.Parameters.Eip214Transition, 0) <= block;
            releaseSpec.IsEip658Enabled = BlockOf(chainSpec.Parameters.Eip658Transition, 0) <= block;
            releaseSpec.IsEip649Enabled = (chainSpec.ByzantiumBlockNumber ?? 0UL) <= block;
            releaseSpec.IsEip1014Enabled = BlockOf(chainSpec.Parameters.Eip1014Transition, 0) <= block;
            releaseSpec.IsEip1052Enabled = BlockOf(chainSpec.Parameters.Eip1052Transition, 0) <= block;
            releaseSpec.IsEip1108Enabled = BlockOf(chainSpec.Parameters.Eip1108Transition) <= block;
            releaseSpec.IsEip1234Enabled = (chainSpec.ConstantinopleBlockNumber ?? chainSpec.ConstantinopleFixBlockNumber ?? 0UL) <= block;
            releaseSpec.IsEip1283Enabled = BlockOf(chainSpec.Parameters.Eip1283Transition) <= block
                && (BlockOf(chainSpec.Parameters.Eip1283DisableTransition) > block
                    || BlockOf(chainSpec.Parameters.Eip1283ReenableTransition) <= block);
            releaseSpec.IsEip1344Enabled = BlockOf(chainSpec.Parameters.Eip1344Transition) <= block;
            releaseSpec.IsEip1884Enabled = BlockOf(chainSpec.Parameters.Eip1884Transition) <= block;
            releaseSpec.IsEip2028Enabled = BlockOf(chainSpec.Parameters.Eip2028Transition) <= block;
            releaseSpec.IsEip2200Enabled = BlockOf(chainSpec.Parameters.Eip2200Transition) <= block
                || BlockOf(chainSpec.Parameters.Eip1706Transition) <= block && releaseSpec.IsEip1283Enabled;
            releaseSpec.IsEip1559Enabled = BlockOf(chainSpec.Parameters.Eip1559Transition) <= block;
            releaseSpec.Eip1559TransitionBlock = BlockOf(chainSpec.Parameters.Eip1559Transition);
            releaseSpec.IsEip2537Enabled = BlockOf(chainSpec.Parameters.Eip2537Transition) <= block ||
                                           (chainSpec.Parameters.Eip2537TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.IsEip2565Enabled = BlockOf(chainSpec.Parameters.Eip2565Transition) <= block;
            releaseSpec.IsEip2929Enabled = BlockOf(chainSpec.Parameters.Eip2929Transition) <= block;
            releaseSpec.IsEip2930Enabled = BlockOf(chainSpec.Parameters.Eip2930Transition) <= block;
            releaseSpec.IsEip3198Enabled = BlockOf(chainSpec.Parameters.Eip3198Transition) <= block;
            releaseSpec.IsEip3541Enabled = BlockOf(chainSpec.Parameters.Eip3541Transition) <= block;
            releaseSpec.IsEip3529Enabled = BlockOf(chainSpec.Parameters.Eip3529Transition) <= block;
            releaseSpec.IsEip3607Enabled = BlockOf(chainSpec.Parameters.Eip3607Transition) <= block;
            releaseSpec.ValidateChainId = BlockOf(chainSpec.Parameters.ValidateChainIdTransition, 0) <= block;
            releaseSpec.ValidateReceipts = ((chainSpec.Parameters.ValidateReceiptsTransition > 0)
                ? Math.Max(BlockOf(chainSpec.Parameters.ValidateReceiptsTransition, 0),
                           BlockOf(chainSpec.Parameters.Eip658Transition, 0))
                : 0UL) <= block;
            releaseSpec.Eip1559BaseFeeMinValue = releaseSpec.IsEip1559Enabled
                && BlockOf(chainSpec.Parameters.Eip1559BaseFeeMinValueTransition) <= block
                    ? chainSpec.Parameters.Eip1559BaseFeeMinValue
                    : null;
            releaseSpec.ElasticityMultiplier = chainSpec.Parameters.Eip1559ElasticityMultiplier ?? Eip1559Constants.DefaultElasticityMultiplier;
            releaseSpec.ForkBaseFee = chainSpec.Parameters.Eip1559BaseFeeInitialValue ?? Eip1559Constants.DefaultForkBaseFee;
            releaseSpec.BaseFeeMaxChangeDenominator = chainSpec.Parameters.Eip1559BaseFeeMaxChangeDenominator ?? Eip1559Constants.DefaultBaseFeeMaxChangeDenominator;

            releaseSpec.IsEip1153Enabled = BlockOf(chainSpec.Parameters.Eip1153Transition) <= block ||
                                          (chainSpec.Parameters.Eip1153TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.IsEip3651Enabled = BlockOf(chainSpec.Parameters.Eip3651Transition) <= block ||
                                          (chainSpec.Parameters.Eip3651TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.IsEip3855Enabled = BlockOf(chainSpec.Parameters.Eip3855Transition) <= block ||
                                           (chainSpec.Parameters.Eip3855TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.IsEip3860Enabled = BlockOf(chainSpec.Parameters.Eip3860Transition) <= block ||
                                           (chainSpec.Parameters.Eip3860TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.IsEip4895Enabled = (chainSpec.Parameters.Eip4895TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.WithdrawalTimestamp = chainSpec.Parameters.Eip4895TransitionTimestamp ?? ulong.MaxValue;

            releaseSpec.IsEip4844Enabled = BlockOf(chainSpec.Parameters.Eip4844Transition) <= block ||
                (chainSpec.Parameters.Eip4844TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.IsEip7951Enabled = (chainSpec.Parameters.Eip7951TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.IsRip7212Enabled = (chainSpec.Parameters.Rip7212TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.Eip4844TransitionTimestamp = chainSpec.Parameters.Eip4844TransitionTimestamp ?? ulong.MaxValue;
            releaseSpec.IsEip5656Enabled = BlockOf(chainSpec.Parameters.Eip5656Transition) <= block ||
                                           (chainSpec.Parameters.Eip5656TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.IsEip6780Enabled = BlockOf(chainSpec.Parameters.Eip6780Transition) <= block ||
                                           (chainSpec.Parameters.Eip6780TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.IsEip4788Enabled = (chainSpec.Parameters.Eip4788TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.Eip4788ContractAddress = chainSpec.Parameters.Eip4788ContractAddress;
            releaseSpec.IsEip2935Enabled = (chainSpec.Parameters.Eip2935TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.Eip2935ContractAddress = chainSpec.Parameters.Eip2935ContractAddress;
            releaseSpec.Eip2935RingBufferSize = chainSpec.Parameters.Eip2935RingBufferSize;

            releaseSpec.IsEip7702Enabled = (chainSpec.Parameters.Eip7702TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.IsEip7823Enabled = (chainSpec.Parameters.Eip7823TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;

            releaseSpec.IsEip6110Enabled = (chainSpec.Parameters.Eip6110TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.DepositContractAddress = chainSpec.Parameters.DepositContractAddress;

            releaseSpec.IsEip7002Enabled = (chainSpec.Parameters.Eip7002TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.Eip7002ContractAddress = chainSpec.Parameters.Eip7002ContractAddress;

            releaseSpec.IsEip7251Enabled = (chainSpec.Parameters.Eip7251TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.Eip7251ContractAddress = chainSpec.Parameters.Eip7251ContractAddress;
            releaseSpec.IsEip7623Enabled = (chainSpec.Parameters.Eip7623TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.IsEip7976Enabled = (chainSpec.Parameters.Eip7976TransitionTimestamp ?? chainSpec.AmsterdamTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.IsEip7981Enabled = (chainSpec.Parameters.Eip7981TransitionTimestamp ?? chainSpec.AmsterdamTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.IsEip7883Enabled = (chainSpec.Parameters.Eip7883TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;

            releaseSpec.IsEip7594Enabled = (chainSpec.Parameters.Eip7594TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.IsEip7825Enabled = (chainSpec.Parameters.Eip7825TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.IsEip7918Enabled = (chainSpec.Parameters.Eip7918TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.IsEip8024Enabled = (chainSpec.Parameters.Eip8024TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.IsEip8246Enabled = (chainSpec.Parameters.Eip8246TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.IsEip8038Enabled = (chainSpec.Parameters.Eip8038TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;

            bool eip1559FeeCollector = releaseSpec.IsEip1559Enabled && BlockOf(chainSpec.Parameters.Eip1559FeeCollectorTransition) <= block;
            bool eip4844FeeCollector = releaseSpec.IsEip4844Enabled && (chainSpec.Parameters.Eip4844FeeCollectorTransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.FeeCollector = (eip1559FeeCollector || eip4844FeeCollector) ? chainSpec.Parameters.FeeCollector : null;
            releaseSpec.IsEip4844FeeCollectorEnabled = eip4844FeeCollector;

            releaseSpec.IsEip7934Enabled = (chainSpec.Parameters.Eip7934TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.Eip7934MaxRlpBlockSize = chainSpec.Parameters.Eip7934MaxRlpBlockSize;

            releaseSpec.IsEip7939Enabled = (chainSpec.Parameters.Eip7939TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;

            releaseSpec.IsEip8037Enabled = (chainSpec.Parameters.Eip8037TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.IsEip7778Enabled = (chainSpec.Parameters.Eip7778TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;

            releaseSpec.IsEip7928Enabled = (chainSpec.Parameters.Eip7928TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            releaseSpec.IsEip7843Enabled = (chainSpec.Parameters.Eip7843TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;

            releaseSpec.IsEip7708Enabled = (chainSpec.Parameters.Eip7708TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;

            releaseSpec.IsEip7954Enabled = (chainSpec.Parameters.Eip7954TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            if (releaseSpec.IsEip7954Enabled)
            {
                releaseSpec.MaxCodeSize = CodeSizeConstants.MaxCodeSizeEip7954;
            }

            releaseSpec.IsEip2780Enabled = (chainSpec.Parameters.Eip2780TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
            if (releaseSpec.IsEip2780Enabled && !releaseSpec.IsEip7708Enabled)
            {
                // The EIP-2780 value-transfer cost prices the EIP-7708 transfer log, so activating
                // it without EIP-7708 would charge for a log that is never emitted.
                throw new ArgumentException($"{nameof(chainSpec.Parameters.Eip2780TransitionTimestamp)} requires EIP-7708 to be active at the same time.");
            }

            foreach (IChainSpecEngineParameters item in _chainSpec.EngineChainSpecParametersProvider
                         .AllChainSpecParameters)
            {
                item.ApplyToReleaseSpec(releaseSpec, block, releaseStartTimestamp);
            }

            SetBlobScheduleParameters();

            return releaseSpec;

            void SetBlobScheduleParameters()
            {
                if (releaseSpec.Eip4844TransitionTimestamp > releaseStartTimestamp)
                {
                    return;
                }

                BlobScheduleSettings? blobSchedule = chainSpec.Parameters.BlobSchedule?.OrderByDescending(bs => bs).FirstOrDefault(bs => bs.Timestamp <= releaseStartTimestamp);

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

        private static bool IsPostMergeRelease(ChainSpec chainSpec, ulong releaseStartBlock, ulong? releaseStartTimestamp) =>
            chainSpec.Parameters.TerminalTotalDifficulty?.IsZero == true
            || releaseStartBlock > (chainSpec.Parameters.TerminalPoWBlockNumber ?? ulong.MaxValue)
            || (chainSpec.Parameters.Eip4895TransitionTimestamp ?? ulong.MaxValue) <= (releaseStartTimestamp ?? 0);

        public void UpdateMergeTransitionInfo(ulong? blockNumber, UInt256? terminalTotalDifficulty = null)
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

        public ulong? DaoBlockNumber => _chainSpec.DaoForkBlockNumber;
        public ulong? BeaconChainGenesisTimestamp => _chainSpec.Parameters.BeaconChainGenesisTimestamp;

        public ulong NetworkId => _chainSpec.NetworkId;
        public ulong ChainId => _chainSpec.ChainId;
        public string SealEngine => _chainSpec.SealEngineType;
        public IEnumerable<string> AvailableForks => _forkAware?.AvailableForks ?? [];

        public bool TryGetForkSpec(string forkName, out IReleaseSpec? spec)
        {
            spec = null;
            return _forkAware?.TryGetForkSpec(forkName, out spec) ?? false;
        }
    }
}
