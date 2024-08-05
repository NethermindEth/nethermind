// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Logging;
using Google.Protobuf;

namespace Nethermind.Merge.AuRa.Shutter;

using G1 = Bls.P1;

public class ShutterMessageHandler(
    IShutterConfig shutterConfig,
    ShutterTxSource txSource,
    ShutterEon eon,
    ILogManager logManager) : IShutterMessageHandler
{
    private readonly ILogger _logger = logManager.GetClassLogger();
    private readonly ulong _instanceId = shutterConfig.InstanceID;

    public void OnDecryptionKeysReceived(Dto.DecryptionKeys decryptionKeys)
    {
        ulong loadedTransactionsSlot = txSource.HighestLoadedSlot();

        if (decryptionKeys.Gnosis.Slot <= loadedTransactionsSlot)
        {
            if (_logger.IsDebug) _logger.Debug($"Skipping Shutter decryption keys from slot {decryptionKeys.Gnosis.Slot}, keys currently stored for slot {loadedTransactionsSlot}.");
            return;
        }

        ShutterEon.Info? eonInfo = eon.GetCurrentEonInfo();
        if (eonInfo is null)
        {
            if (_logger.IsDebug) _logger.Debug("Cannot check Shutter decryption keys, eon info was not found.");
            return;
        }

        if (_logger.IsDebug) _logger.Debug($"Checking Shutter decryption keys instanceID: {decryptionKeys.InstanceID} eon: {decryptionKeys.Eon} #keys: {decryptionKeys.Keys.Count} #sig: {decryptionKeys.Gnosis.Signatures.Count()} #txpointer: {decryptionKeys.Gnosis.TxPointer} #slot: {decryptionKeys.Gnosis.Slot}");

        if (CheckDecryptionKeys(decryptionKeys, eonInfo.Value))
        {
            if (_logger.IsInfo) _logger.Info($"Validated Shutter decryption keys for slot {decryptionKeys.Gnosis.Slot}");

            List<(byte[], byte[])> keys = decryptionKeys.Keys.Select(x => (x.Identity.ToByteArray(), x.Key_.ToByteArray())).ToList();
            txSource.LoadTransactions(decryptionKeys.Eon, decryptionKeys.Gnosis.TxPointer, decryptionKeys.Gnosis.Slot, keys);
        }
    }

    private bool CheckDecryptionKeys(in Dto.DecryptionKeys decryptionKeys, in ShutterEon.Info eonInfo)
    {
        if (decryptionKeys.InstanceID != _instanceId)
        {
            if (_logger.IsDebug) _logger.Debug($"Invalid Shutter decryption keys received: instanceID {decryptionKeys.InstanceID} did not match expected value {_instanceId}.");
            return false;
        }

        if (decryptionKeys.Eon != eonInfo.Eon)
        {
            if (_logger.IsDebug) _logger.Debug($"Invalid Shutter decryption keys received: eon {decryptionKeys.Eon} did not match expected value {eonInfo.Eon}.");
            return false;
        }

        if (decryptionKeys.Keys.Count == 0)
        {
            if (_logger.IsDebug) _logger.Error("Invalid Shutter decryption keys received: expected placeholder key.");
            return false;
        }

        // skip placeholder transaction
        foreach (Dto.Key key in decryptionKeys.Keys.AsEnumerable().Skip(1))
        {
            G1 dk, identity;
            try
            {
                dk = new(key.Key_.ToArray());
                identity = ShutterCrypto.ComputeIdentity(key.Identity.Span);
            }
            catch (Bls.Exception e)
            {
                if (_logger.IsDebug) _logger.Error("Invalid Shutter decryption keys received.", e);
                return false;
            }

            if (!ShutterCrypto.CheckDecryptionKey(dk, eonInfo.Key, identity))
            {
                if (_logger.IsDebug) _logger.Debug("Invalid Shutter decryption keys received: decryption key did not match eon key.");
                return false;
            }
        }

        long signerIndicesCount = decryptionKeys.Gnosis.SignerIndices.LongCount();

        if (decryptionKeys.Gnosis.SignerIndices.Distinct().Count() != signerIndicesCount)
        {
            if (_logger.IsDebug) _logger.Debug("Invalid Shutter decryption keys received: incorrect number of signer indices.");
            return false;
        }

        if (decryptionKeys.Gnosis.Signatures.Count != signerIndicesCount)
        {
            if (_logger.IsDebug) _logger.Debug("Invalid Shutter decryption keys received: incorrect number of signatures.");
            return false;
        }

        if (signerIndicesCount != (int)eonInfo.Threshold)
        {
            if (_logger.IsDebug) _logger.Debug($"Invalid Shutter decryption keys received: signer indices did not match threshold.");
            return false;
        }

        List<byte[]> identityPreimages = decryptionKeys.Keys.Select(key => key.Identity.ToArray()).ToList();

        foreach ((ulong signerIndex, ByteString signature) in decryptionKeys.Gnosis.SignerIndices.Zip(decryptionKeys.Gnosis.Signatures))
        {
            Address keyperAddress = eonInfo.Addresses[signerIndex];

            if (!ShutterCrypto.CheckSlotDecryptionIdentitiesSignature(_instanceId, eonInfo.Eon, decryptionKeys.Gnosis.Slot, decryptionKeys.Gnosis.TxPointer, identityPreimages, signature.Span, keyperAddress))
            {
                if (_logger.IsDebug) _logger.Debug($"Invalid Shutter decryption keys received: bad signature.");
                return false;
            }
        }

        return true;
    }
}
