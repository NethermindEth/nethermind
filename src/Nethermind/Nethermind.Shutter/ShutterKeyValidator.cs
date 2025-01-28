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
using Nethermind.Core.Collections;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Nethermind.Shutter;

using G1 = Bls.P1;
using G1Affine = Bls.P1Affine;
using G2Affine = Bls.P2Affine;

public class ShutterKeyValidator(
    IShutterConfig shutterConfig,
    IShutterEon eon,
    ILogManager logManager) : IShutterKeyValidator
{
    private ulong? _highestValidatedSlot;
    private readonly ILogger _logger = logManager.GetClassLogger();
    private readonly ulong _instanceId = shutterConfig.InstanceID;
    private readonly Lock _lockObject = new();

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

            if (_logger.IsDebug) _logger.Debug($"Checking Shutter decryption keys instanceID: {decryptionKeys.InstanceID} eon: {decryptionKeys.Eon} #keys: {decryptionKeys.Keys.Count} #sig: {decryptionKeys.Gnosis.Signatures.Count} #txpointer: {decryptionKeys.Gnosis.TxPointer} #slot: {decryptionKeys.Gnosis.Slot}");

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

    [SkipLocalsInit]
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
            if (_logger.IsDebug) _logger.Error("DEBUG/ERROR Invalid Shutter decryption keys received: expected placeholder key.");
            return false;
        }

        G1Affine dk = new(stackalloc long[G1Affine.Sz]);
        G1 identity = new(stackalloc long[G1.Sz]);
        G2Affine eonKey = new(stackalloc long[G2Affine.Sz]);

        // skip placeholder transaction
        foreach (Dto.Key key in decryptionKeys.Keys.AsEnumerable().Skip(1))
        {
            try
            {
                dk.Decode(key.Key_.Span);
                ShutterCrypto.ComputeIdentity(identity, key.Identity.Span);
            }
            catch (Bls.BlsException e)
            {
                if (_logger.IsDebug) _logger.Error("DEBUG/ERROR Invalid Shutter decryption keys received.", e);
                return false;
            }

            eonKey.Decode(eonInfo.Key.AsSpan());
            if (!ShutterCrypto.CheckDecryptionKey(dk, eonKey, identity.ToAffine()))
            {
                if (_logger.IsDebug) _logger.Debug("Invalid Shutter decryption keys received: decryption key did not match eon key.");
                return false;
            }
        }

        int signerIndicesCount = decryptionKeys.Gnosis.SignerIndices.Count;

        if (decryptionKeys.Gnosis.SignerIndices.ContainsDuplicates(signerIndicesCount))
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

        IEnumerable<ReadOnlyMemory<byte>> identityPreimages = decryptionKeys.Keys.Select(static key => key.Identity.Memory);

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

    private static EnumerableWithCount<(ReadOnlyMemory<byte>, ReadOnlyMemory<byte>)> ExtractKeys(in Dto.DecryptionKeys decryptionKeys)
        // remove placeholder
        => new(decryptionKeys.Keys.Skip(1).Select(static x => (x.Identity.Memory, x.Key_.Memory)), decryptionKeys.Keys.Count - 1);
}
