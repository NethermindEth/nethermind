// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Int256;
using System.Linq;
using Google.Protobuf;
using Nethermind.Core.Extensions;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Shutter.Test;
using Nethermind.Core;
using System.Collections.Generic;
using Nethermind.Shutter;
using Nethermind.Shutter.Dto;
using Nethermind.Serialization.Rlp;
using Nethermind.Core.Test.Builders;
using Nethermind.Abi;

using G1 = Nethermind.Crypto.Bls.P1;
using G2 = Nethermind.Crypto.Bls.P2;
using EncryptedMessage = Nethermind.Shutter.ShutterCrypto.EncryptedMessage;

namespace Nethermind.Shutter.Test;

public class ShutterEventSimulator
{
    private readonly ulong _defaultGasLimit = 21000;
    private readonly int _defaultMaxKeyCount;
    private readonly Random _rnd;
    private readonly ulong _chainId;
    private readonly ulong _threshold;
    private readonly IAbiEncoder _abiEncoder;
    private readonly Address _sequencerContractAddress;
    private readonly AbiEncodingInfo _transactionSubmittedAbi;
    private ulong _slot;
    private ulong _eon;
    private ulong _txIndex; // for tracking events being emitted
    private ulong _txPointer; // for tracking which keys are released
    private UInt256 _sk;
    private G2 _eonKey;
    private IEnumerable<Event> _eventSource;
    private Queue<(byte[] IdentityPreimage, byte[] Key)> _keys = [];

    public ShutterEventSimulator(
        Random rnd,
        ulong chainId,
        ulong eon,
        ulong threshold,
        ulong slot,
        ulong txIndex,
        IAbiEncoder abiEncoder,
        Address sequencerContractAddress,
        AbiEncodingInfo transactionSubmittedAbi
    )
    {
        _rnd = rnd;
        _chainId = chainId;
        _eon = eon;
        _slot = slot;
        _txIndex = txIndex;
        _txPointer = txIndex;
        _threshold = threshold;
        _abiEncoder = abiEncoder;
        _sequencerContractAddress = sequencerContractAddress;
        _transactionSubmittedAbi = transactionSubmittedAbi;
        _defaultMaxKeyCount = (int)Math.Floor((decimal)ShutterTestsCommon.Cfg.EncryptedGasLimit / _defaultGasLimit);

        NewEon(eon);
        _eventSource = EmitEvents();
    }

    public struct Event
    {
        public byte[] EncryptedTransaction;
        public byte[] IdentityPreimage;
        public byte[] Key;
        public LogEntry LogEntry;
        public byte[] Transaction;
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
            _keys.Enqueue((e.IdentityPreimage, e.Key));
        }

        keyCount ??= Math.Min(_keys.Count, _defaultMaxKeyCount);

        List<(byte[] IdentityPreimage, byte[] Key)> keys = [];
        for (int i = 0; i < keyCount; i++)
        {
            keys.Add(_keys.Dequeue());
        }
        DecryptionKeys decryptionKeys = ToDecryptionKeys(keys, GetCurrentEonInfo(), _txPointer, (int)_threshold);

        _slot++;
        _txPointer += (ulong)keyCount;
        return (events, decryptionKeys);
    }

    protected virtual IEnumerable<Event> EmitEvents()
    {
        return EmitEvents(EmitDefaultGasLimits(), EmitDefaultTransactions());
    }

    protected IEnumerable<Event> EmitEvents(IEnumerable<UInt256> gasLimits, IEnumerable<Transaction> transactions)
    {
        foreach ((UInt256 gasLimit, Transaction tx) in gasLimits.Zip(transactions))
        {
            byte[] identityPreimage = new byte[52];
            byte[] sigma = new byte[32];
            _rnd.NextBytes(identityPreimage);
            _rnd.NextBytes(sigma);

            G1 identity = ShutterCrypto.ComputeIdentity(identityPreimage);
            G1 key = identity.dup().mult(_sk.ToLittleEndian());

            byte[] encodedTx = Rlp.Encode<Transaction>(tx).Bytes;
            EncryptedMessage encryptedMessage = ShutterCrypto.Encrypt(encodedTx, identity, _eonKey, new(sigma));
            byte[] encryptedTx = ShutterCrypto.EncodeEncryptedMessage(encryptedMessage);

            yield return new()
            {
                EncryptedTransaction = encryptedTx,
                IdentityPreimage = identityPreimage,
                Key = key.compress(),
                LogEntry = EncodeShutterLog(encryptedTx, identityPreimage, _txIndex++, gasLimit),
                Transaction = encodedTx
            };
        }
    }

    protected IEnumerable<UInt256> EmitDefaultGasLimits()
    {
        while (true)
        {
            yield return new UInt256(_defaultGasLimit);
        }
    }

    protected IEnumerable<Transaction> EmitDefaultTransactions()
    {
        ulong nonce = 0;
        while (true)
        {
            yield return Build.A.Transaction
                .WithNonce(nonce++)
                .WithChainId(_chainId)
                .WithSenderAddress(TestItem.AddressA)
                .WithTo(TestItem.AddressA)
                .WithValue(100)
                .Signed(TestItem.PrivateKeyA)
                .TestObject;
        }
    }

    public IShutterEon.Info GetCurrentEonInfo()
        => new()
        {
            Eon = _eon,
            Key = _eonKey,
            Threshold = _threshold,
            Addresses = TestItem.Addresses
        };

    public void NewEon()
        => NewEon(_eon + 1);

    private void NewEon(ulong eon)
    {
        byte[] sk = new byte[32];
        _rnd.NextBytes(sk);

        _eon = eon;
        _sk = new(sk);
        _eonKey = G2.generator().mult(_sk.ToLittleEndian());
    }

    private LogEntry EncodeShutterLog(
        ReadOnlySpan<byte> encryptedTransaction,
        ReadOnlySpan<byte> identityPreimage,
        ulong txIndex,
        UInt256 gasLimit)
    {
        byte[] logData = _abiEncoder.Encode(_transactionSubmittedAbi, [
            _eon,
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

    private DecryptionKeys ToDecryptionKeys(List<(byte[] IdentityPreimage, byte[] Key)> rawKeys, in IShutterEon.Info eon, ulong txIndex, int signatureCount)
    {
        rawKeys.Sort((a, b) => Bytes.BytesComparer.Compare(a.IdentityPreimage, b.IdentityPreimage));
        rawKeys.Insert(0, ([], []));

        var keys = rawKeys.Select(k => new Key()
        {
            Identity = ByteString.CopyFrom(k.IdentityPreimage),
            Key_ = ByteString.CopyFrom(k.Key),
        }).ToList();

        var identityPreimages = rawKeys.Select(k => k.IdentityPreimage).ToList();
        var randomIndices = Enumerable.Range(0, TestItem.PublicKeys.Length).Shuffle(_rnd).ToList();

        List<ulong> signerIndices = [];
        List<ByteString> signatures = [];

        for (int i = 0; i < signatureCount; i++)
        {
            ulong index = (ulong)randomIndices[i];
            PrivateKey sk = TestItem.PrivateKeys[index];
            Hash256 h = ShutterCrypto.GenerateHash(ShutterTestsCommon.Cfg.InstanceID, eon.Eon, _slot, txIndex, identityPreimages);
            byte[] sig = ShutterTestsCommon.Ecdsa.Sign(sk, h).BytesWithRecovery;
            signerIndices.Add(index);
            signatures.Add(ByteString.CopyFrom(sig));
        }

        GnosisDecryptionKeysExtra gnosis = new()
        {
            Slot = _slot,
            TxPointer = txIndex,
            SignerIndices = { signerIndices },
            Signatures = { signatures }
        };

        return new()
        {
            InstanceID = ShutterTestsCommon.Cfg.InstanceID,
            Eon = eon.Eon,
            Keys = { keys },
            Gnosis = gnosis
        };
    }
}
