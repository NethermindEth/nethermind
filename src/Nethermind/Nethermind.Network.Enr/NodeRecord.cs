//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Enr;

public class NodeRecord
{
    private int _enrSequence;

    private string? _enrString;

    private Keccak? _contentHash;

    private SortedDictionary<string, EnrContentEntry> Entries { get; } = new();

    public byte[]? OriginalContentRlp { get; set; }

    public int EnrSequence
    {
        get => _enrSequence;
        set
        {
            if (_enrSequence != value)
            {
                _enrSequence = value;
                _enrString = null;
                _contentHash = null;
                Signature = null;
            }
        }
    }

    public string EnrString
    {
        get
        {
            return _enrString ??= CreateEnrString();
        }
    }

    public Keccak ContentHash
    {
        get
        {
            return _contentHash ??= CalculateContentHash();
        }
    }

    private Keccak CalculateContentHash()
    {
        KeccakRlpStream rlpStream = new();
        EncodeContent(rlpStream);
        return rlpStream.GetHash();
    }

    public Signature? Signature { get; private set; }

    public NodeRecord()
    {
        SetEntry(IdEntry.Instance);
    }

    public void Seal(Signature signature)
    {
        // verify here?
        Signature = signature;
    }

    public void SetEntry(EnrContentEntry entry)
    {
        if (Entries.ContainsKey(entry.Key))
        {
            EnrSequence++;
        }

        Entries[entry.Key] = entry;
    }

    public TValue? GetValue<TValue>(string entryKey) where TValue : struct
    {
        if (Entries.ContainsKey(entryKey))
        {
            EnrContentEntry<TValue> entry = (EnrContentEntry<TValue>)Entries[entryKey];
            return entry.Value;
        }

        return null;
    }

    public TValue? GetObj<TValue>(string entryKey) where TValue : class
    {
        if (Entries.ContainsKey(entryKey))
        {
            EnrContentEntry<TValue> entry = (EnrContentEntry<TValue>)Entries[entryKey];
            return entry.Value;
        }

        return null;
    }

    private int GetContentLengthWithoutSignature()
    {
        int contentLength =
            Rlp.LengthOf(EnrSequence); // this is a different meaning of a sequence than the RLP sequence
        foreach ((_, EnrContentEntry enrContentEntry) in Entries)
        {
            contentLength += enrContentEntry.GetRlpLength();
        }

        return contentLength;
    }

    private int GetContentLengthWithSignature()
    {
        return GetContentLengthWithoutSignature() + 64 + 2;
    }

    public int GetRlpLengthWithSignature()
    {
        return Rlp.LengthOfSequence(
            GetContentLengthWithSignature());
    }

    public void EncodeContent(RlpStream rlpStream)
    {
        int contentLength = GetContentLengthWithoutSignature();
        rlpStream.StartSequence(contentLength);
        rlpStream.Encode(EnrSequence);
        foreach ((_, EnrContentEntry contentEntry) in Entries.OrderBy(e => e.Key))
        {
            contentEntry.Encode(rlpStream);
        }
    }

    public string GetHex()
    {
        int contentLength = GetContentLengthWithSignature();
        int totalLength = Rlp.LengthOfSequence(contentLength);
        RlpStream rlpStream = new(totalLength);
        Encode(rlpStream);
        return rlpStream.Data!.ToHexString();
    }

    public void Encode(RlpStream rlpStream)
    {
        RequireSignature();

        int contentLength = GetContentLengthWithSignature();
        rlpStream.StartSequence(contentLength);
        rlpStream.Encode(Signature!.Bytes);
        rlpStream.Encode(EnrSequence); // a different sequence here (not RLP sequence)
        foreach ((_, EnrContentEntry contentEntry) in Entries.OrderBy(e => e.Key))
        {
            contentEntry.Encode(rlpStream);
        }
    }

    private string CreateEnrString()
    {
        RequireSignature();

        int rlpLength = GetRlpLengthWithSignature();
        RlpStream rlpStream = new(rlpLength);
        Encode(rlpStream);
        byte[] rlpData = rlpStream.Data!;
        // Console.WriteLine("actual: " + rlpData.ToHexString());
        // https://tools.ietf.org/html/rfc4648#section-5

        // Base64Url must be used, hence Replace calls (not sure if allocating internally)
        return string.Concat("enr:",
            Convert.ToBase64String(rlpData).Replace("+", "-").Replace("/", "_").Replace("=", ""));
    }

    private void RequireSignature()
    {
        if (Signature is null)
        {
            throw new Exception("Cannot encode a node record with an empty signature.");
        }
    }
}
