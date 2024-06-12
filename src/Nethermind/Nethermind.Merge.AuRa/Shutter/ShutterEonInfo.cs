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

public class ShutterEonInfo
{
    public ulong Eon { get; private set; } = uint.MaxValue;
    public Bls.P2 Key { get; private set; }
    public ulong Threshold { get; private set; }
    public Address[] Addresses { get; private set; } = [];
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

    public void Update(BlockHeader header)
    {
        Hash256 stateRoot = _readOnlyBlockTree.Head!.StateRoot!;
        IReadOnlyTransactionProcessor readOnlyTransactionProcessor = _readOnlyTxProcessingEnvFactory.Create().Build(stateRoot);

        try
        {
            KeyperSetManagerContract keyperSetManagerContract = new(readOnlyTransactionProcessor, _abiEncoder, KeyperSetManagerContractAddress);
            ulong eon = keyperSetManagerContract.GetKeyperSetIndexByBlock(header, (ulong)header.Number + 1);

            if (Eon != eon)
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
                lock (Addresses)
                {
                    Eon = eon;
                    Key = key;
                    Threshold = threshold;
                    Addresses = addresses;

                    if (_logger.IsInfo) _logger.Info($"Shutter eon: {Eon} threshold: {Threshold} #keypers: {Addresses.Length}");
                }
            }
        }
        catch (AbiException e)
        {
            if (_logger.IsDebug) _logger.Debug($"Error when calling Shutter Keyper contracts: {e}");
        }
    }
}
