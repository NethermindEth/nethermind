// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Common.Utilities;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.TxPool;
using Nethermind.Wallet;
using Nethermind.Xdc.Spec;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using static Microsoft.FSharp.Core.ByRefKinds;
using static System.Net.Mime.MediaTypeNames;

namespace Nethermind.Xdc;

internal class SignTransactionManager(IDb stateDb, ISigner signer, ITxPool txPool) : ISignTransactionManager
{
    public async Task CreateTransactionSign(XdcBlockHeader header, IXdcReleaseSpec spec)
    {
        UInt256 nonce = txPool.GetLatestPendingNonce(signer.Address);
        Transaction transaction = CreateTxSign((UInt256)header.Number, header.Hash, nonce, spec.BlockSignersAddress, signer.Address);

        await signer.Sign(transaction);

        // add local somehow to tx pool
        bool added = txPool.SubmitTx(transaction, TxHandlingOptions.PersistentBroadcast);
        if (!added)
        {
            throw new Exception("Failed to add signed transaction to the pool.");
        }

        long blockNumber = header.Number;
        long checkNumber = blockNumber % spec.EpochLength;

        PrivateKeyGenerator privateKeyGenerator = new PrivateKeyGenerator();
        PrivateKey randomPrivate = privateKeyGenerator.Generate();

        byte[] randomKey = Encoding.UTF8.GetBytes("randomizeKey");

        var exists = stateDb.KeyExists(randomKey);

        if (exists)
        {
            if (checkNumber > 0 && spec.EpochBlockOpening <= checkNumber && spec.EpochBlockRandomize >= checkNumber)
            {
                var randomizeKeyValue = stateDb.Get(randomKey);
                Transaction tx = CreateTxOpeningRandomize(nonce + 1, spec.RandomizeSMCBinary, randomizeKeyValue, signer.Address);
                await signer.Sign(tx);

                // add local somehow to tx pool
                bool addedOpening = txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);

                stateDb.Remove(randomKey);
            }
        }
        else
        {
            var randomizeKeyValue = RandStringByte(32);
            if (checkNumber > 0 && spec.EpochBlockSecret <= checkNumber && spec.EpochBlockOpening > checkNumber)
            {
                Transaction tx = BuildTxSecretRandomize(nonce + 1, spec.RandomizeSMCBinary, (ulong)spec.EpochLength, randomizeKeyValue, signer.Address);
                await signer.Sign(tx);
                // add local somehow to tx pool
                bool addedOpening = txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);

                stateDb.PutSpan(randomKey, randomizeKeyValue);
            }
        }
    }

    internal static Transaction CreateTxSign(UInt256 number, Hash256 hash, UInt256 nonce, Address blockSignersAddress, Address sender)
    {
        byte[] inputData = [.. XdcConstants.SignMethod, .. number.PaddedBytes(32), .. hash.Bytes.PadLeft(32)];

        var transaction = new Transaction();
        transaction.Nonce = nonce;
        transaction.To = blockSignersAddress;
        transaction.Value = 0;
        transaction.GasLimit = 200_000;
        transaction.GasPrice = 0;
        transaction.Data = inputData;
        transaction.SenderAddress = sender;

        transaction.Type = TxType.Legacy;

        transaction.Hash = transaction.CalculateHash();

        return transaction;
    }

    internal static Transaction CreateTxOpeningRandomize(UInt256 nonce, Address randomizeSMCBinary, byte[] randomizeKey, Address sender)
    {
        byte[] inputData = [.. XdcConstants.SetOpening, .. randomizeKey];

        var transaction = new Transaction();
        transaction.Nonce = nonce;
        transaction.To = randomizeSMCBinary;
        transaction.Value = 0;
        transaction.GasLimit = 200_000;
        transaction.GasPrice = 0;
        transaction.Data = inputData;
        transaction.SenderAddress = sender;

        transaction.Type = TxType.Legacy;

        transaction.Hash = transaction.CalculateHash();

        return transaction;
    }

    internal static Transaction BuildTxSecretRandomize(UInt256 nonce, Address randomizeSMCBinary, ulong epochNumber, byte[] randomizeKey, Address sender)
    {
        var secretNumb = RandomNumberGenerator.GetInt32((int)epochNumber);

        var secrets = new long[] { secretNumb };
        const int sizeOfArray = 32;

        var arrSizeOfSecrets = (UInt256)sizeOfArray;
        var arrLengthOfSecrets = (UInt256)secrets.Length;

        List<byte> input = [.. XdcConstants.SetSecret, .. arrSizeOfSecrets.PaddedBytes(32), .. arrLengthOfSecrets.PaddedBytes(32)];

        foreach (var secret in secrets)
        {
            var enc = Encrypt(randomizeKey, secret.ToString()); // base64-url string
            var encBytes = Encoding.UTF8.GetBytes(enc);
            var padded = encBytes.PadLeft(sizeOfArray);
            input.AddRange(padded);
        }

        var inputData = input.ToArray();
        // Build TransactionInput (no from set here). Caller may sign/create raw tx with nonce/gas/value.
        var transaction = new Transaction();
        transaction.Nonce = nonce;
        transaction.To = randomizeSMCBinary;
        transaction.Value = 0;
        transaction.GasLimit = 200_000;
        transaction.GasPrice = 0;
        transaction.Data = inputData;
        transaction.SenderAddress = sender;

        transaction.Type = TxType.Legacy;

        transaction.Hash = transaction.CalculateHash();

        return transaction;
    }

    private static string Encrypt(byte[] randomizeKey, string text)
    {
        using var aes = Aes.Create();
        aes.Key = randomizeKey;
        aes.Mode = CipherMode.CFB;
        aes.Padding = PaddingMode.None;
        aes.BlockSize = 128;

        var iv = new byte[aes.BlockSize / 8];
        RandomNumberGenerator.Fill(iv);

        var plaintext = Encoding.UTF8.GetBytes(text);
        using var ms = new MemoryStream();
        // prepend IV
        ms.Write(iv, 0, iv.Length);
        using (var encryptor = aes.CreateEncryptor(aes.Key, iv))
        using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
        {
            cs.Write(plaintext, 0, plaintext.Length);
            cs.FlushFinalBlock();
        }

        var cipherBytes = ms.ToArray();
        return Convert.ToBase64String(cipherBytes);
    }

    internal static byte[] RandStringByte(int n)
    {
        const string letterBytes = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ123456789";
        var result = new byte[n];
        for (int i = 0; i < n; i++)
        {
            int idx = RandomNumberGenerator.GetInt32(letterBytes.Length);
            result[i] = (byte)letterBytes[idx];
        }
        return result;
    }
}
