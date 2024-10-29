// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO;
using Nethermind.Crypto;
using Nethermind.Serialization.Json;

using G1Affine = Nethermind.Crypto.Bls.P1Affine;

namespace Nethermind.Shutter.Config;

public class ValidatorsInfo
{
    public bool IsEmpty { get => _indexToPubKey is null || _indexToPubKey.Count == 0; }
    public IEnumerable<ulong> ValidatorIndices { get => _indexToPubKey!.Keys; }
    private Dictionary<ulong, byte[]>? _indexToPubKey;

    public void Load(string fp)
    {
        FileStream fstream = new(fp, FileMode.Open, FileAccess.Read, FileShare.None);
        _indexToPubKey = new EthereumJsonSerializer().Deserialize<Dictionary<ulong, byte[]>>(fstream);
    }

    public bool Validate(out string err)
    {
        if (_indexToPubKey is null)
        {
            err = "Validator info file not loaded.";
            return false;
        }

        G1Affine pk = new(stackalloc long[G1Affine.Sz]);

        foreach ((ulong index, byte[] pubkey) in _indexToPubKey)
        {
            if (!pk.TryDecode(pubkey, out Bls.ERROR _))
            {
                err = $"Validator info file contains invalid public key with index {index}";
                return false;
            }
        }

        err = "";
        return true;
    }

    public bool IsIndexRegistered(ulong index)
        => _indexToPubKey!.ContainsKey(index);

    public byte[] GetPubKey(ulong index)
        => _indexToPubKey![index];
}
