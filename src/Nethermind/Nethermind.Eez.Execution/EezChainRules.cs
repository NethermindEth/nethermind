// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Eez.Execution.Settlement.RollupX;

namespace Nethermind.Eez.Execution;

/// <summary>
/// The execution rules of one EEZ L2 chain. A genesis whose configuration has a <c>rollupX</c> section follows
/// RollupX; any other follows rollup0. The two differ in the execution manager's address, the gas of system
/// transactions, a balance burn at every block start, the EIP-1559 denominator and whether the EIP-4788 call and
/// execution requests run.
/// </summary>
public sealed class EezChainRules
{
    /// <summary>The gas limit of every RollupX block, without the Ethereum 1/1024 drift.</summary>
    public const ulong RollupXBlockGasLimit = 30_000_000;

    /// <summary>RollupX's EIP-1559 base fee max change denominator.</summary>
    public const ulong RollupXBaseFeeMaxChangeDenominator = 50;

    /// <summary>The cap on a RollupX system transaction's gas, which otherwise takes what the block has left.</summary>
    public static readonly ulong RollupXSystemTxGasCap = Eip7825Constants.DefaultTxGasLimitCap;

    private static readonly Address[] RollupXBurned = [EezConstants.RollupXEezl2Address, EezConstants.SystemAddress];
    private static readonly Address[] NativeRollupXBurned = [EezConstants.RollupXEezl2Address, Address.SystemUser];

    private readonly bool _skipConsensusLayerCalls;

    private EezChainRules(Address eezl2Address, RollupXSchedule? schedule, Address? feeCollector = null, bool skipConsensusLayerCalls = false,
        TxType systemTransactionType = EezConstants.SystemTxType, NativeSystemTxFormat nativeFormat = NativeSystemTxFormat.RecordedV1, uint nativeHistoryOffset = 0)
    {
        _skipConsensusLayerCalls = skipConsensusLayerCalls;
        Eezl2Address = eezl2Address;
        Schedule = schedule;
        FeeCollector = feeCollector;
        SystemTransactionType = systemTransactionType;
        NativeFormat = nativeFormat;
        NativeHistoryOffset = nativeHistoryOffset;
    }

    public static EezChainRules Rollup0 { get; } = new(EezConstants.Eezl2Address, null);

    public static EezChainRules RollupX(RollupXSchedule schedule) => new(EezConstants.RollupXEezl2Address, schedule);

    internal static EezChainRules NativeRollupX(RollupXSchedule schedule, Address feeCollector, NativeSystemTxFormat format = NativeSystemTxFormat.RecordedV1, uint historyOffset = 0)
    {
        if (format is not (NativeSystemTxFormat.RecordedV1 or NativeSystemTxFormat.ContractV3 or NativeSystemTxFormat.ContractV4 or NativeSystemTxFormat.ContractV6))
            throw new ArgumentOutOfRangeException(nameof(format));
        if (feeCollector == Address.Zero || feeCollector == Address.SystemUser || feeCollector == EezConstants.RollupXEezl2Address)
            throw new ArgumentException("Native fee collector uses a reserved address.", nameof(feeCollector));
        return new EezChainRules(EezConstants.RollupXEezl2Address, schedule, feeCollector,
            systemTransactionType: EezConstants.NativeSystemTxType, nativeFormat: format, nativeHistoryOffset: historyOffset);
    }

    /// <exception cref="InvalidDataException">The genesis has a <c>rollupX</c> section that breaks a RollupX rule.</exception>
    public static EezChainRules FromGenesis(JsonElement genesis)
    {
        if (genesis.ValueKind != JsonValueKind.Object
            || (genesis.TryGetProperty("config", out JsonElement section) && section.ValueKind != JsonValueKind.Object))
        {
            throw new InvalidDataException("An EEZ genesis and its config must be JSON objects.");
        }

        if (genesis.TryGetProperty("config", out JsonElement nativeConfig) &&
            nativeConfig.TryGetProperty("rollupX", out JsonElement nativeSection) && nativeSection.ValueKind == JsonValueKind.Object &&
            (nativeSection.TryGetProperty("historyUpdateOffset", out _) || nativeSection.TryGetProperty("feeCollector", out _)))
            return NativeGenesisSnapshot.Load(JsonSerializer.SerializeToUtf8Bytes(genesis), NativeSystemTxFormat.ContractV6).Rules;
        RollupXSchedule? schedule = RollupXGenesis.Read(genesis);
        if (!genesis.TryGetProperty("config", out JsonElement config) || !config.TryGetProperty("feeCollector", out JsonElement collector))
        {
            return schedule is null ? Rollup0 : RollupX(schedule);
        }

        if (schedule is not null || collector.ValueKind != JsonValueKind.String || !Address.TryParse(collector.GetString(), out Address? address))
        {
            throw new InvalidDataException("feeCollector must be a 20-byte address on a rollup0 chain");
        }

        return new EezChainRules(EezConstants.Eezl2Address, null, address);
    }

    /// <exception cref="InvalidDataException">The file is not JSON, or its <c>rollupX</c> section breaks a RollupX rule.</exception>
    public static EezChainRules FromGenesisFile(string path)
    {
        using FileStream file = File.OpenRead(path);
        try
        {
            using JsonDocument document = JsonDocument.Parse(file);
            return FromGenesis(document.RootElement);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{path} is not a JSON genesis: {e.Message}", e);
        }
    }

    /// <summary>The L2 execution manager, the only target of a system transaction.</summary>
    public Address Eezl2Address { get; }

    /// <summary>RollupX's block grid; <c>null</c> under rollup0, whose timing is not part of consensus.</summary>
    public RollupXSchedule? Schedule { get; }

    /// <summary>The fixed genesis recipient of ordinary transaction base fees; null preserves burn behavior.</summary>
    public Address? FeeCollector { get; }

    internal TxType SystemTransactionType { get; }
    internal NativeSystemTxFormat NativeFormat { get; }
    internal uint NativeHistoryOffset { get; }
    internal bool UsesNativeContract => UsesNativeSystemTransactions && NativeFormat is NativeSystemTxFormat.ContractV3 or NativeSystemTxFormat.ContractV4 or NativeSystemTxFormat.ContractV6;
    internal bool UsesNativeSystemTransactions => SystemTransactionType == EezConstants.NativeSystemTxType;
    internal Address SystemAddress => UsesNativeSystemTransactions ? Address.SystemUser : EezConstants.SystemAddress;

    public bool IsRollupX => Schedule is not null;

    /// <summary>
    /// The accounts whose balance RollupX zeroes before the first transaction of every block, after the EIP-2935
    /// history update: ether left on the execution manager or sent to the system address does not carry over.
    /// </summary>
    public Address[] BurnedAtBlockStart => FinalizesProtocolBalances ? [] : ProtocolBalanceAccounts;
    internal Address[] ProtocolBalanceAccounts => UsesNativeSystemTransactions ? NativeRollupXBurned : IsRollupX ? RollupXBurned : [];
    internal bool FinalizesProtocolBalances => UsesNativeSystemTransactions && NativeFormat == NativeSystemTxFormat.ContractV6;

    /// <summary>The EIP-1559 denominator the chain uses instead of the fork's, if any.</summary>
    public ulong? BaseFeeMaxChangeDenominator => IsRollupX ? RollupXBaseFeeMaxChangeDenominator : null;

    /// <summary>Whether a block calls the EIP-4788 beacon roots contract; RollupX headers carry a zero root and skip the call.</summary>
    public bool CallsBeaconRootsContract => !IsRollupX && !_skipConsensusLayerCalls;

    /// <summary>Whether blocks read deposits and request contracts; RollupX blocks never carry requests.</summary>
    public bool ReadsExecutionRequests => !IsRollupX && !_skipConsensusLayerCalls;

    internal EezChainRules WithoutConsensusLayerCalls() => new(Eezl2Address, Schedule, FeeCollector, true, SystemTransactionType, NativeFormat, NativeHistoryOffset);

    /// <summary>
    /// The gas a RollupX system transaction executes with, given the gas the block has used before it: what is left,
    /// up to the cap. <c>null</c> under rollup0, whose system transactions keep their fixed budget.
    /// </summary>
    public ulong? SystemTransactionGas(BlockHeader header) =>
        IsRollupX ? Math.Min(header.GasLimit - Math.Min(header.GasUsed, header.GasLimit), RollupXSystemTxGasCap) : null;

    /// <summary>Whether <paramref name="gasLimit"/> is one a system transaction of this chain can carry.</summary>
    public bool IsSystemTransactionGas(ulong gasLimit) =>
        IsRollupX ? gasLimit <= RollupXSystemTxGasCap : gasLimit == EezConstants.SystemTxGasLimit;

    internal bool HasNativePositionGas(Transaction tx, BlockHeader header) =>
        header.GasUsed <= header.GasLimit && tx.GasLimit == Math.Min(header.GasLimit - header.GasUsed,
            tx.To == EezConstants.L1HistoryAddress ? EezConstants.L1HistoryTxGasCap : RollupXSystemTxGasCap);
}
