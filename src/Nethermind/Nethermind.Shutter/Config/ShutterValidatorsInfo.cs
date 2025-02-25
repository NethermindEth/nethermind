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
    public bool IsEmpty { get => _indexToPubKey is null || _indexToPubKey.Count == 0; }
    public IEnumerable<ulong> ValidatorIndices { get => _indexToPubKey!.Keys; }
    public class ShutterValidatorsInfoException(string message) : Exception(message);

    protected readonly Dictionary<ulong, long[]> _indexToPubKey = [];
    protected ulong _minIndex = ulong.MaxValue;
    protected ulong _maxIndex = ulong.MinValue;

    public void Load(string fp)
    {
        FileStream fstream = new(fp, FileMode.Open, FileAccess.Read, FileShare.Read);
        Dictionary<ulong, byte[]> indexToPubKeyBytes = new EthereumJsonSerializer().Deserialize<Dictionary<ulong, byte[]>>(fstream);
        AddPublicKeys(indexToPubKeyBytes);
    }

    public bool ContainsIndex(ulong index)
        => _indexToPubKey!.ContainsKey(index);

    // non inclusive of end index
    public bool MayContainIndexInRange(ulong startIndex, ulong endIndex)
        => (endIndex <= _maxIndex && endIndex > _minIndex) || (startIndex < _maxIndex && startIndex >= _minIndex);

    public G1Affine GetPubKey(ulong index)
        => new(_indexToPubKey[index]);

    internal void Add(ulong index, long[] pubkey)
    {
        _indexToPubKey.Add(index, pubkey);
        _minIndex = Math.Min(_minIndex, index);
        _maxIndex = Math.Max(_maxIndex, index + 1);
    }

    private void AddPublicKeys(Dictionary<ulong, byte[]> indexToPubKeyBytes)
    {
        G1Affine pk = new(stackalloc long[G1Affine.Sz]);

        foreach ((ulong index, byte[] pubkey) in indexToPubKeyBytes)
        {
            if (!pk.TryDecode(pubkey, out Bls.ERROR _))
            {
                throw new ShutterValidatorsInfoException($"Validator info file contains invalid public key with index {index}.");
            }

            Add(index, pk.Point.ToArray());
        }
    }
}
