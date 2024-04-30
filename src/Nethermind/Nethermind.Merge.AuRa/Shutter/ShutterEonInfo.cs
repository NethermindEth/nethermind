using Nethermind.Blockchain;
using System;
using System.Linq;
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

public class ShutterEonInfo
{
    public ulong Eon = uint.MaxValue;
    public Bls.P2 Key;
    public ulong Threshold;
    public Address[] Addresses = [];
    private readonly IReadOnlyBlockTree _readOnlyBlockTree;
    private readonly ReadOnlyTxProcessingEnvFactory _readOnlyTxProcessingEnvFactory;
    private readonly IAbiEncoder _abiEncoder;
    private readonly ILogger _logger;
    private readonly Address KeyBroadcastContractAddress;
    private readonly Address KeyperSetManagerContractAddress;

    public ShutterEonInfo(IReadOnlyBlockTree readOnlyBlockTree, ReadOnlyTxProcessingEnvFactory readOnlyTxProcessingEnvFactory, IAbiEncoder abiEncoder, IAuraConfig auraConfig, ILogger logger)
    {
        _readOnlyBlockTree = readOnlyBlockTree;
        _readOnlyTxProcessingEnvFactory = readOnlyTxProcessingEnvFactory;
        _abiEncoder = abiEncoder;
        _logger = logger;
        KeyBroadcastContractAddress = new(auraConfig.ShutterKeyBroadcastContractAddress);
        KeyperSetManagerContractAddress = new(auraConfig.ShutterKeyperSetManagerContractAddress);
    }

    public bool Update()
    {
        Hash256 stateRoot = _readOnlyBlockTree.Head!.StateRoot!;
        IReadOnlyTransactionProcessor readOnlyTransactionProcessor = _readOnlyTxProcessingEnvFactory.Create().Build(stateRoot);
        BlockHeader header = _readOnlyBlockTree.Head!.Header;
        KeyperSetManagerContract keyperSetManagerContract = new(readOnlyTransactionProcessor, _abiEncoder, KeyperSetManagerContractAddress);

        long bestSuggestedNumber = _readOnlyBlockTree.FindBestSuggestedHeader()?.Number ?? 0;
        long headNumberOrZero = _readOnlyBlockTree.Head?.Number ?? 0;
        bool isSyncing = bestSuggestedNumber > headNumberOrZero + 8;

        if (isSyncing)
        {
            return false;
        }

        ulong nextBlockNumber = (ulong)header.Number + 1;
        ulong eon = keyperSetManagerContract.GetKeyperSetIndexByBlock(header, nextBlockNumber);

        if (Eon != eon)
        {
            Eon = eon;

            Address keyperSetContractAddress = keyperSetManagerContract.GetKeyperSetAddress(header, eon);
            KeyperSetContract keyperSetContract = new(readOnlyTransactionProcessor, _abiEncoder, keyperSetContractAddress);
            if (!keyperSetContract.IsFinalized(header))
            {
                throw new Exception("Cannot use unfinalized keyper set contract.");
            }
            Threshold = keyperSetContract.GetThreshold(header);
            Addresses = keyperSetContract.GetMembers(header);

            KeyBroadcastContract keyBroadcastContract = new(readOnlyTransactionProcessor, _abiEncoder, KeyBroadcastContractAddress);
            byte[] eonKeyBytes = keyBroadcastContract.GetEonKey(_readOnlyBlockTree.Head!.Header, eon);

            Key = new(eonKeyBytes);

            if (_logger.IsInfo) _logger.Info($"Shutter eon: {Eon} key: {Convert.ToHexString(eonKeyBytes)} threshold: {Threshold} #keyperAddresses: {Addresses.Length}");
        }

        return true;
    }
}
