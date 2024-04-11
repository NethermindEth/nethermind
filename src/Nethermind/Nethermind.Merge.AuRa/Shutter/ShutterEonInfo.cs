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

namespace Nethermind.Merge.AuRa.Shutter;

public class ShutterEonInfo
{
    public ulong Eon = uint.MaxValue;
    public Bls.P2 Key;
    public ulong Threshold;
    public Address[] Addresses = [];
    private readonly IReadOnlyBlockTree _readOnlyBlockTree;
    private readonly IReadOnlyTxProcessorSource _readOnlyTxProcessorSource;
    private readonly IAbiEncoder _abiEncoder;
    private readonly ILogger _logger;
    private readonly Address KeyBroadcastContractAddress;
    private readonly Address KeyperSetManagerContractAddress;
    
    public ShutterEonInfo(IReadOnlyBlockTree readOnlyBlockTree, ReadOnlyTxProcessingEnvFactory readOnlyTxProcessingEnvFactory, IAbiEncoder abiEncoder, IAuraConfig auraConfig, ILogger logger)
    {
        _readOnlyBlockTree = readOnlyBlockTree;
        _readOnlyTxProcessorSource = readOnlyTxProcessingEnvFactory.Create();
        _abiEncoder = abiEncoder;
        _logger = logger;
        KeyBroadcastContractAddress = new(auraConfig.ShutterKeyBroadcastContractAddress);
        KeyperSetManagerContractAddress = new(auraConfig.ShutterKeyperSetManagerContractAddress);
    }

    public void Update()
    {
        IReadOnlyTransactionProcessor readOnlyTransactionProcessor = _readOnlyTxProcessorSource.Build(_readOnlyBlockTree.Head!.StateRoot!);
        BlockHeader header = _readOnlyBlockTree.Head!.Header;
        KeyperSetManagerContract keyperSetManagerContract = new(readOnlyTransactionProcessor, _abiEncoder, KeyperSetManagerContractAddress);

        ulong nextBlockNumber = (ulong)header.Number + 1;
        (Address keyperSetContractAddress, ulong eon) = keyperSetManagerContract.GetKeyperSetIndexByBlock(header, nextBlockNumber);

        if (Eon == eon)
        {
            return;
        }
        else
        {
            Eon = eon;

            KeyperSetContract keyperSetContract = new(readOnlyTransactionProcessor, _abiEncoder, keyperSetContractAddress);
            if (!keyperSetContract.IsFinalized(header))
            {
                throw new Exception("Cannot use unfinalized keyper set contract.");
            }
            Threshold = keyperSetContract.GetThreshold(header);
            Addresses = keyperSetContract.GetMembers(header);

            KeyBroadcastContract keyBroadcastContract = new(readOnlyTransactionProcessor, _abiEncoder, KeyBroadcastContractAddress);
            byte[] eonKeyBytes = keyBroadcastContract.GetEonKey(_readOnlyBlockTree.Head!.Header, eon);
            // todo: remove once shutter fixes
            if (!eonKeyBytes.Any())
            {
                // eonKeyBytes = Convert.FromHexString("2fdfb787563ac3aa9be365a581eae6684334cbb9ce11e95c486ea31820e0469a07a5e6e49caddee2b1891900848e7ed03749aac68d4d31d4f98f4a537b9050621a791a11c6c154ae972659a5a4ed7c55d2bf8772f1a4c05542436df59d0a2edc05ea7e70b72f27b4eb8a4fb5ed675cb35d67934a1ed75043ed3802ac6a8ed68c");
                eonKeyBytes = Convert.FromHexString("B068AD1BE382009AC2DCE123EC62DCA8337D6B93B909B3EE52E31CB9E4098D1B56D596BF3C08166C7B46CB3AA85C23381380055AB9F1A87786F2508F3E4CE5CAA5ABCDAE0A80141EE8CCC3626311E0A53BE5D873FA964FD85AD56771F2984579");
            }
            Key = new(eonKeyBytes);

            if (_logger.IsInfo) _logger.Info($"Shutter eon: {Eon} key: {Convert.ToHexString(eonKeyBytes)} threshold: {Threshold} #keyperAddresses: {Addresses.Length}");
        }
    }
}
