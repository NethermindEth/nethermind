// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Shutter.Config;
using Nethermind.Logging;
using Google.Protobuf;

namespace Nethermind.Shutter;

using G1 = Bls.P1;

public class ShutterKeyValidator(
    IShutterConfig shutterConfig,
    IShutterEon eon,
    ILogManager logManager) : IShutterKeyValidator
{
    private ulong? _highestValidatedSlot;
    private readonly ILogger _logger = logManager.GetClassLogger();
    private readonly ulong _instanceId = shutterConfig.InstanceID;
    private readonly object _lockObject = new();

    public IShutterKeyValidator.ValidatedKeys? ValidateKeys(Dto.DecryptionKeys decryptionKeys)
    {
        lock (_lockObject)
        {
            if (_highestValidatedSlot is not null && decryptionKeys.Gnosis.Slot <= _highestValidatedSlot)
            {
                if (_logger.IsDebug) _logger.Debug($"Skipping Shutter decryption keys from slot {decryptionKeys.Gnosis.Slot}, keys currently stored for slot {_highestValidatedSlot}.");
                return null;
            }

            IShutterEon.Info? eonInfo = eon.GetCurrentEonInfo();
            if (eonInfo is null)
            {
                if (_logger.IsDebug) _logger.Debug("Cannot check Shutter decryption keys, eon info was not found.");
                return null;
            }

            if (_logger.IsDebug) _logger.Debug($"Checking Shutter decryption keys instanceID: {decryptionKeys.InstanceID} eon: {decryptionKeys.Eon} #keys: {decryptionKeys.Keys.Count} #sig: {decryptionKeys.Gnosis.Signatures.Count()} #txpointer: {decryptionKeys.Gnosis.TxPointer} #slot: {decryptionKeys.Gnosis.Slot}");

            if (CheckDecryptionKeys(decryptionKeys, eonInfo.Value))
            {
                if (_logger.IsInfo) _logger.Info($"Validated Shutter decryption keys for slot {decryptionKeys.Gnosis.Slot}.");
                _highestValidatedSlot = decryptionKeys.Gnosis.Slot;
                return new()
                {
                    Eon = decryptionKeys.Eon,
                    Slot = decryptionKeys.Gnosis.Slot,
                    TxPointer = decryptionKeys.Gnosis.TxPointer,
                    Keys = ExtractKeys(decryptionKeys)
                };
            }
            else
            {
                return null;
            }
        }
    }

    private bool CheckDecryptionKeys(in Dto.DecryptionKeys decryptionKeys, in IShutterEon.Info eonInfo)
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

            if (!ShutterCrypto.CheckDecryptionKey(dk, new(eonInfo.Key), identity))
            {
                if (_logger.IsDebug) _logger.Debug("Invalid Shutter decryption keys received: decryption key did not match eon key.");
                return false;
            }
        }

        int signerIndicesCount = decryptionKeys.Gnosis.SignerIndices.Count;

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

        var identityPreimages = decryptionKeys.Keys.Select(key => key.Identity.ToArray()).ToList();

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

    private static List<(byte[], byte[])> ExtractKeys(in Dto.DecryptionKeys decryptionKeys)
        => decryptionKeys.Keys
            .Skip(1) // remove placeholder
            .Select(x => (x.Identity.ToByteArray(), x.Key_.ToByteArray())).ToList();
}
