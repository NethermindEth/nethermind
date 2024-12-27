// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Int256;
using System.Linq;
using Google.Protobuf;
using Nethermind.Core.Extensions;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Core;
using System.Collections.Generic;
using Nethermind.Shutter.Dto;
using Nethermind.Serialization.Rlp;
using Nethermind.Core.Test.Builders;
using Nethermind.Abi;

using G1 = Nethermind.Crypto.Bls.P1;
using G2 = Nethermind.Crypto.Bls.P2;
using EncryptedMessage = Nethermind.Shutter.ShutterCrypto.EncryptedMessage;
using Nethermind.Shutter.Contracts;

namespace Nethermind.Shutter.Test;

public class ShutterEventSimulator
{
    private const ulong DefaultGasLimit = 21000;
    private readonly int _defaultMaxKeyCount;
    private readonly Random _rnd;
    private readonly ulong _chainId;
    private readonly ulong _threshold;
    private readonly IAbiEncoder _abiEncoder;
    private readonly Address _sequencerContractAddress;
    private readonly AbiEncodingInfo _transactionSubmittedAbi;
    private ulong _slot;
    protected ulong _eon;
    private ulong _txPointer; // for tracking which keys are released
    private readonly IEnumerable<Event> _eventSource;
    private readonly Queue<(byte[] IdentityPreimage, byte[] Key)>[] _keys = new Queue<(byte[] IdentityPreimage, byte[] Key)>[10];
    private readonly EonData[] _eonData = new EonData[10];

    public ShutterEventSimulator(
        Random rnd,
        ulong chainId,
        ulong threshold,
        ulong slot,
        IAbiEncoder abiEncoder,
        Address sequencerContractAddress
    )
    {
        _rnd = rnd;
        _chainId = chainId;
        _eon = 0;
        _slot = slot;
        _txPointer = 0;
        _threshold = threshold;
        _abiEncoder = abiEncoder;
        _sequencerContractAddress = sequencerContractAddress;
        _transactionSubmittedAbi = new SequencerContract(sequencerContractAddress).TransactionSubmittedAbi;
        _defaultMaxKeyCount = (int)Math.Floor((decimal)ShutterTestsCommon.Cfg.EncryptedGasLimit / DefaultGasLimit);

        _eventSource = EmitEvents();

        for (ulong i = 0; i < 10; i++)
        {
            _eonData[i] = new EonData(_rnd, i);
            _keys[i] = [];
        }
    }

    public struct Event
    {
        public byte[] EncryptedTransaction;
        public byte[] IdentityPreimage;
        public byte[] Key;
        public LogEntry LogEntry;
        public byte[] Transaction;
        public ulong Eon;
    }

    public List<Event> GetEvents(int c)
    {
        return _eventSource.Take(c).ToList();
    }

    public (List<Event> events, DecryptionKeys keys) AdvanceSlot(int eventCount, int? keyCount)
    {
        var events = _eventSource.Take(eventCount).ToList();
        foreach (Event e in events)
        {
            _keys[e.Eon].Enqueue((e.IdentityPreimage, e.Key));
        }

        keyCount ??= Math.Min(_keys[_eon].Count, _defaultMaxKeyCount);

        List<(byte[] IdentityPreimage, byte[] Key)> keys = [];
        for (int i = 0; i < keyCount; i++)
        {
            keys.Add(_keys[_eon].Dequeue());
        }
        DecryptionKeys decryptionKeys = ToDecryptionKeys(keys, _txPointer, (int)_threshold);

        _slot++;
        _txPointer += (ulong)keyCount;
        return (events, decryptionKeys);
    }

    protected virtual IEnumerable<Event> EmitEvents()
    {
        return EmitEvents(EmitDefaultEons(), EmitDefaultTransactions());
    }

    protected IEnumerable<Event> EmitEvents(IEnumerable<ulong> eons, IEnumerable<Transaction> transactions)
    {
        foreach ((ulong eon, Transaction tx) in eons.Zip(transactions))
        {
            byte[] identityPreimage = new byte[52];
            byte[] sigma = new byte[32];
            _rnd.NextBytes(identityPreimage);
            _rnd.NextBytes(sigma);

            ulong txIndex = _eonData[eon].TxIndex++;
            G1 identity = new();
            ShutterCrypto.ComputeIdentity(identity, identityPreimage);
            G1 key = identity.Dup().Mult(_eonData[eon].SecretKey.ToLittleEndian());

            byte[] encodedTx = Rlp.Encode(tx, RlpBehaviors.SkipTypedWrapping).Bytes;
            EncryptedMessage encryptedMessage = ShutterCrypto.Encrypt(encodedTx, identity, new(_eonData[eon].Key), new(sigma));
            byte[] encryptedTx = ShutterCrypto.EncodeEncryptedMessage(encryptedMessage).ToArray();

            yield return new()
            {
                EncryptedTransaction = encryptedTx,
                IdentityPreimage = identityPreimage,
                Key = key.Compress(),
                LogEntry = EncodeShutterLog(encryptedTx, identityPreimage, eon, txIndex, DefaultGasLimit),
                Transaction = encodedTx,
                Eon = eon
            };
        }
    }

    protected IEnumerable<ulong> EmitDefaultEons()
    {
        while (true)
        {
            yield return _eon;
        }
    }

    protected IEnumerable<Transaction> EmitDefaultTransactions()
    {
        ulong nonce = 0;
        // alternate legacy and type 2 transactions
        bool type2 = false;
        while (true)
        {
            TransactionBuilder<Transaction> txBuilder = Build.A.Transaction
                .WithNonce(nonce++)
                .WithChainId(_chainId)
                .WithSenderAddress(TestItem.AddressA)
                .WithTo(TestItem.AddressA)
                .WithValue(100);

            if (type2)
            {
                txBuilder = txBuilder
                    .WithType(TxType.EIP1559)
                    .WithMaxFeePerGas(4)
                    .WithGasLimit(21000);

                type2 = false;
            }
            else
            {
                type2 = true;
            }

            yield return txBuilder.Signed(TestItem.PrivateKeyA).TestObject;
        }
    }

    public void NextEon()
    {
        _eon++;
        _txPointer = 0;
    }

    public IShutterEon.Info GetCurrentEonInfo()
    {
        EonData eonData = _eonData.ElementAt((int)_eon);
        return new()
        {
            Eon = _eon,
            Key = new G2(eonData.Key.AsSpan()).Compress(),
            Threshold = _threshold,
            Addresses = TestItem.Addresses
        };
    }

    private LogEntry EncodeShutterLog(
        ReadOnlySpan<byte> encryptedTransaction,
        ReadOnlySpan<byte> identityPreimage,
        ulong eon,
        ulong txIndex,
        UInt256 gasLimit)
    {
        byte[] logData = _abiEncoder.Encode(_transactionSubmittedAbi, [
            eon,
            txIndex,
            identityPreimage[..32].ToArray(),
            new Address(identityPreimage[32..].ToArray()),
            encryptedTransaction.ToArray(),
            gasLimit
        ]);

        return Build.A.LogEntry
            .WithAddress(_sequencerContractAddress)
            .WithTopics(_transactionSubmittedAbi.Signature.Hash)
            .WithData(logData)
            .TestObject;
    }

    private DecryptionKeys ToDecryptionKeys(List<(byte[] IdentityPreimage, byte[] Key)> rawKeys, ulong txPointer, int signatureCount)
    {
        rawKeys.Sort(static (a, b) => Bytes.BytesComparer.Compare(a.IdentityPreimage, b.IdentityPreimage));
        rawKeys.Insert(0, ([], []));

        var keys = rawKeys.Select(static k => new Key()
        {
            Identity = ByteString.CopyFrom(k.IdentityPreimage),
            Key_ = ByteString.CopyFrom(k.Key),
        }).ToList();

        IEnumerable<ReadOnlyMemory<byte>> identityPreimages = rawKeys.Select(static k => (ReadOnlyMemory<byte>)k.IdentityPreimage);
        List<int> randomIndices = Enumerable.Range(0, TestItem.PublicKeys.Length).Shuffle(_rnd).ToList();

        List<ulong> signerIndices = [];
        List<ByteString> signatures = [];

        for (int i = 0; i < signatureCount; i++)
        {
            ulong index = (ulong)randomIndices[i];
            PrivateKey sk = TestItem.PrivateKeys[index];
            Hash256 h = ShutterCrypto.GenerateHash(ShutterTestsCommon.Cfg.InstanceID, _eon, _slot, txPointer, identityPreimages);
            byte[] sig = ShutterTestsCommon.Ecdsa.Sign(sk, h).BytesWithRecovery;
            signerIndices.Add(index);
            signatures.Add(ByteString.CopyFrom(sig));
        }

        GnosisDecryptionKeysExtra gnosis = new()
        {
            Slot = _slot,
            TxPointer = txPointer,
            SignerIndices = { signerIndices },
            Signatures = { signatures }
        };

        return new()
        {
            InstanceID = ShutterTestsCommon.Cfg.InstanceID,
            Eon = _eon,
            Keys = { keys },
            Gnosis = gnosis
        };
    }

    private struct EonData
    {
        public ulong Eon { get; }
        public byte[] Key { get; }
        public UInt256 SecretKey { get; }
        public ulong TxIndex { get; set; }

        public EonData(Random rnd, ulong eon)
        {
            byte[] sk = new byte[32];
            rnd.NextBytes(sk);

            Eon = eon;
            SecretKey = new(sk);
            Key = G2.Generator().Mult(SecretKey.ToLittleEndian()).Compress();
            TxIndex = 0;
        }
    }
}
