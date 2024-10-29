// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Nethermind.Crypto;
using Nethermind.Serialization.Json;

using G1Affine = Nethermind.Crypto.Bls.P1Affine;

namespace Nethermind.Shutter.Config;

public class ShutterValidatorsInfo
{
    public bool IsEmpty { get => _indexToPubKeyBytes is null || _indexToPubKeyBytes.Count == 0; }
    public IEnumerable<ulong> ValidatorIndices { get => _indexToPubKeyBytes!.Keys; }
    public class ShutterValidatorsInfoException(string message) : Exception(message);

    private Dictionary<ulong, byte[]>? _indexToPubKeyBytes;
    private readonly Dictionary<ulong, long[]> _indexToPubKey = [];

    public void Load(string fp)
    {
        FileStream fstream = new(fp, FileMode.Open, FileAccess.Read, FileShare.None);
        _indexToPubKeyBytes = new EthereumJsonSerializer().Deserialize<Dictionary<ulong, byte[]>>(fstream);
    }

    public void Validate()
    {
        G1Affine pk = new(stackalloc long[G1Affine.Sz]);

        foreach ((ulong index, byte[] pubkey) in _indexToPubKeyBytes!)
        {
            if (!pk.TryDecode(pubkey, out Bls.ERROR _))
            {
                throw new ShutterValidatorsInfoException($"Validator info file contains invalid public key with index {index}.");
            }

            _indexToPubKey.Add(index, pk.Point.ToArray());
        }
    }

    public bool IsIndexRegistered(ulong index)
        => _indexToPubKeyBytes!.ContainsKey(index);

    public G1Affine GetPubKey(ulong index)
        => new(_indexToPubKey[index]);
}
