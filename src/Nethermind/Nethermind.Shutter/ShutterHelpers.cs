// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Shutter.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Serialization.Json;
using Multiformats.Address;

namespace Nethermind.Shutter;
public static class ShutterHelpers
{
    public static TimeSpan SlotLength => GnosisSpecProvider.SlotLength;
    public class ShutterSlotCalulationException(string message, Exception? innerException = null) : Exception(message, innerException);

    public static ulong GetGenesisTimestampMs(ISpecProvider specProvider) => 1000 * (specProvider.ChainId == BlockchainIds.Chiado ? ChiadoSpecProvider.BeaconChainGenesisTimestamp : GnosisSpecProvider.BeaconChainGenesisTimestamp);

    public static ulong GetSlotTimestampMs(ulong slot, ulong genesisTimestampMs)
        => genesisTimestampMs + (ulong)SlotLength.TotalMilliseconds * slot;

    public static long GetCurrentOffsetMs(ulong slot, ulong genesisTimestampMs, ulong? slotTimestampMs = null)
        => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (long)(slotTimestampMs ?? GetSlotTimestampMs(slot, genesisTimestampMs));

    public static (ulong slot, long slotOffset) GetBuildingSlotAndOffset(ulong slotTimestampMs, ulong genesisTimestampMs)
    {
        long slotTimeSinceGenesis = (long)slotTimestampMs - (long)genesisTimestampMs;
        if (slotTimeSinceGenesis < 0)
        {
            throw new ShutterSlotCalulationException($"Slot timestamp {slotTimestampMs}ms was greater than genesis timestamp {genesisTimestampMs}ms.");
        }

        ulong buildingSlot = (ulong)slotTimeSinceGenesis / (ulong)SlotLength.TotalMilliseconds;
        long offset = GetCurrentOffsetMs(buildingSlot, genesisTimestampMs, slotTimestampMs);

        return (buildingSlot, offset);
    }

    public static void ValidateConfig(IShutterConfig shutterConfig)
    {
        if (shutterConfig.Validator && shutterConfig.ValidatorInfoFile is null)
        {
            throw new ArgumentException($"Must set Shutter.ValidatorInfoFile to a valid json file.");
        }

        if (shutterConfig.ValidatorInfoFile is not null && !File.Exists(shutterConfig.ValidatorInfoFile))
        {
            throw new ArgumentException($"Shutter validator info file \"{shutterConfig.ValidatorInfoFile}\" does not exist.");
        }

        if (shutterConfig.SequencerContractAddress is null || !Address.TryParse(shutterConfig.SequencerContractAddress, out _))
        {
            throw new ArgumentException("Must set Shutter sequencer contract address to valid address.");
        }

        if (shutterConfig.ValidatorRegistryContractAddress is null || !Address.TryParse(shutterConfig.ValidatorRegistryContractAddress, out _))
        {
            throw new ArgumentException("Must set Shutter validator registry contract address to valid address.");
        }

        if (shutterConfig.KeyBroadcastContractAddress is null || !Address.TryParse(shutterConfig.KeyBroadcastContractAddress, out _))
        {
            throw new ArgumentException("Must set Shutter key broadcast contract address to valid address.");
        }

        if (shutterConfig.KeyperSetManagerContractAddress is null || !Address.TryParse(shutterConfig.KeyperSetManagerContractAddress, out _))
        {
            throw new ArgumentException("Must set Shutter keyper set manager contract address to valid address.");
        }

        if (shutterConfig.P2PAgentVersion is null)
        {
            throw new ArgumentNullException(nameof(shutterConfig.P2PAgentVersion));
        }

        if (shutterConfig.P2PProtocolVersion is null)
        {
            throw new ArgumentNullException(nameof(shutterConfig.P2PProtocolVersion));
        }

        if (shutterConfig.KeyperP2PAddresses is null)
        {
            throw new ArgumentNullException(nameof(shutterConfig.KeyperP2PAddresses));
        }

        foreach (string addr in shutterConfig.KeyperP2PAddresses)
        {
            try
            {
                Multiaddress.Decode(addr);
            }
            catch (NotSupportedException)
            {
                throw new ArgumentException($"Could not decode Shutter keyper p2p address \"{addr}\".");
            }
        }
    }

    public static Dictionary<ulong, byte[]> LoadValidatorInfo(string fp)
    {
        FileStream fstream = new FileStream(fp, FileMode.Open, FileAccess.Read, FileShare.None);
        return new EthereumJsonSerializer().Deserialize<Dictionary<ulong, byte[]>>(fstream);
    }
}
