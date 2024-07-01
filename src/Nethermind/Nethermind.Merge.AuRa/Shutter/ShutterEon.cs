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

public class ShutterEon(
    IReadOnlyBlockTree readOnlyBlockTree,
    ReadOnlyTxProcessingEnvFactory readOnlyTxProcessingEnvFactory,
    IAbiEncoder abiEncoder,
    IShutterConfig shutterConfig,
    ILogger logger)
{
    private Info? _info;
    private readonly Address _keyBroadcastContractAddress = new(shutterConfig.KeyBroadcastContractAddress!);
    private readonly Address _keyperSetManagerContractAddress = new(shutterConfig.KeyperSetManagerContractAddress!);

    public Info? GetCurrentEonInfo() => _info;

    public void Update(BlockHeader header)
    {
        Hash256 stateRoot = readOnlyBlockTree.Head!.StateRoot!;
        IReadOnlyTransactionProcessor processor = readOnlyTxProcessingEnvFactory.Create().Build(stateRoot);

        try
        {
            KeyperSetManagerContract keyperSetManagerContract = new(processor, abiEncoder, _keyperSetManagerContractAddress);
            ulong eon = keyperSetManagerContract.GetKeyperSetIndexByBlock(header, (ulong)header.Number + 1);

            if (_info is null || _info.Value.Eon != eon)
            {
                Address keyperSetContractAddress = keyperSetManagerContract.GetKeyperSetAddress(header, eon);
                KeyperSetContract keyperSetContract = new(processor, abiEncoder, keyperSetContractAddress);

                if (keyperSetContract.IsFinalized(header))
                {
                    ulong threshold = keyperSetContract.GetThreshold(header);
                    Address[] addresses = keyperSetContract.GetMembers(header);

                    KeyBroadcastContract keyBroadcastContract = new(processor, abiEncoder, _keyBroadcastContractAddress);
                    byte[] eonKeyBytes = keyBroadcastContract.GetEonKey(readOnlyBlockTree.Head!.Header, eon);
                    Bls.P2 key = new(eonKeyBytes);

                    // update atomically
                    _info = new()
                    {
                        Eon = eon,
                        Key = key,
                        Threshold = threshold,
                        Addresses = addresses
                    };

                    if (logger.IsInfo) logger.Info($"Shutter eon: {_info.Value.Eon} threshold: {_info.Value.Threshold} #keypers: {_info.Value.Addresses.Length}");
                }
                else
                {
                    if (logger.IsError) logger.Error("Cannot use unfinalised keyper set contract.");
                }
            }
        }
        catch (AbiException e)
        {
            if (logger.IsError) logger.Error($"Error when calling Shutter Keyper contracts", e);
        }
        catch (Bls.Exception e)
        {
            if (logger.IsError) logger.Error($"Invalid Shutter Eon key ", e);
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
