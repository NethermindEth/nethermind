// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Int256;

using Nethermind.Core;
using System.Collections.Generic;
using Nethermind.Shutter;
using Nethermind.Serialization.Rlp;
using Nethermind.Core.Test.Builders;
using Nethermind.Abi;

using G1 = Nethermind.Crypto.Bls.P1;
using G2 = Nethermind.Crypto.Bls.P2;
using EncryptedMessage = Nethermind.Shutter.ShutterCrypto.EncryptedMessage;
using System.Linq;

public class ShutterEventEmitter
{
    private readonly UInt256 _defaultGasLimit = new(21000);
    private readonly Transaction _defaultTx;
    private readonly Random _rnd;
    private readonly ulong _chainId;
    private readonly IAbiEncoder _abiEncoder;
    private readonly Address _sequencerContractAddress;
    private readonly AbiEncodingInfo _transactionSubmittedAbi;
    private ulong _eon;
    private ulong _txIndex;
    private UInt256 _sk;
    private G2 _eonKey;

    public ShutterEventEmitter(
        Random rnd,
        ulong chainId,
        ulong eon,
        ulong txIndex,
        IAbiEncoder abiEncoder,
        Address sequencerContractAddress,
        AbiEncodingInfo transactionSubmittedAbi
    )
    {
        _rnd = rnd;
        _chainId = chainId;
        _eon = eon;
        _txIndex = txIndex;
        _abiEncoder = abiEncoder;
        _sequencerContractAddress = sequencerContractAddress;
        _transactionSubmittedAbi = transactionSubmittedAbi;
        _defaultTx = Build.A.Transaction.WithChainId(_chainId).Signed().TestObject;

        NewEon(eon);
    }

    public struct Event
    {
        public byte[] EncryptedTransaction;
        public byte[] IdentityPreimage;
        public byte[] Key;
        public LogEntry LogEntry;
        public byte[] Transaction;
    }

    public IEnumerable<Event> EmitEvents()
    {
        return EmitEvents(EmitDefaultGasLimits(), EmitDefaultTransactions());
    }

    public IEnumerable<Event> EmitEvents(IEnumerable<UInt256> gasLimits, IEnumerable<Transaction> transactions)
    {
        foreach((UInt256 gasLimit, Transaction tx) in gasLimits.Zip(transactions))
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

    public IEnumerable<UInt256> EmitDefaultGasLimits()
    {
        while (true)
        {
            yield return _defaultGasLimit;
        }
    }

    public IEnumerable<Transaction> EmitDefaultTransactions()
    {
        while (true)
        {
            yield return _defaultTx;
        }
    }

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
}