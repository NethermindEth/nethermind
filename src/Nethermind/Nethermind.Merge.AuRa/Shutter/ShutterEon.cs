// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Crypto;
using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Abi;
using Nethermind.Merge.AuRa.Shutter.Contracts;
using Nethermind.Logging;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core.Crypto;

namespace Nethermind.Merge.AuRa.Shutter;

public class ShutterEon
{
    private Info? _info;
    private readonly IReadOnlyBlockTree _readOnlyBlockTree;
    private readonly ReadOnlyTxProcessingEnvFactory _readOnlyTxProcessingEnvFactory;
    private readonly IAbiEncoder _abiEncoder;
    private readonly ILogger _logger;
    private readonly Address KeyBroadcastContractAddress;
    private readonly Address KeyperSetManagerContractAddress;

    public ShutterEon(IReadOnlyBlockTree readOnlyBlockTree, ReadOnlyTxProcessingEnvFactory readOnlyTxProcessingEnvFactory, IAbiEncoder abiEncoder, IAuraConfig auraConfig, ILogger logger)
    {
        _readOnlyBlockTree = readOnlyBlockTree;
        _readOnlyTxProcessingEnvFactory = readOnlyTxProcessingEnvFactory;
        _abiEncoder = abiEncoder;
        _logger = logger;
        KeyBroadcastContractAddress = new(auraConfig.ShutterKeyBroadcastContractAddress);
        KeyperSetManagerContractAddress = new(auraConfig.ShutterKeyperSetManagerContractAddress);
    }

    public Info? GetCurrentEonInfo()
    {
        return _info;
    }

    public void Update(BlockHeader header)
    {
        Hash256 stateRoot = _readOnlyBlockTree.Head!.StateRoot!;
        ITransactionProcessor readOnlyTransactionProcessor = _readOnlyTxProcessingEnvFactory.Create().Build(stateRoot).TransactionProcessor;

        try
        {
            KeyperSetManagerContract keyperSetManagerContract = new(readOnlyTransactionProcessor, _abiEncoder, KeyperSetManagerContractAddress);
            ulong eon = keyperSetManagerContract.GetKeyperSetIndexByBlock(header, (ulong)header.Number + 1);

            if (_info is null || _info.Value.Eon != eon)
            {
                Address keyperSetContractAddress = keyperSetManagerContract.GetKeyperSetAddress(header, eon);
                KeyperSetContract keyperSetContract = new(readOnlyTransactionProcessor, _abiEncoder, keyperSetContractAddress);

                if (!keyperSetContract.IsFinalized(header))
                {
                    if (_logger.IsError) _logger.Error("Cannot use unfinalised keyper set contract.");
                    return;
                }

                ulong threshold = keyperSetContract.GetThreshold(header);
                Address[] addresses = keyperSetContract.GetMembers(header);

                KeyBroadcastContract keyBroadcastContract = new(readOnlyTransactionProcessor, _abiEncoder, KeyBroadcastContractAddress);
                byte[] eonKeyBytes = keyBroadcastContract.GetEonKey(_readOnlyBlockTree.Head!.Header, eon);
                Bls.P2 key = new(eonKeyBytes);

                // update atomically
                _info = new()
                {
                    Eon = eon,
                    Key = key,
                    Threshold = threshold,
                    Addresses = addresses
                };

                if (_logger.IsInfo) _logger.Info($"Shutter eon: {_info.Value.Eon} threshold: {_info.Value.Threshold} #keypers: {_info.Value.Addresses.Length}");
            }
        }
        catch (AbiException e)
        {
            if (_logger.IsDebug) _logger.Debug($"Error when calling Shutter Keyper contracts: {e}");
        }
    }

    public readonly struct Info
    {
        public ulong Eon { get; init; }
        public Bls.P2 Key { get; init; }
        public ulong Threshold { get; init; }
        public Address[] Addresses { get; init; }
    }
}
