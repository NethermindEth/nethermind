// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Enr;

/// <summary>
/// An entry storing the signature scheme version - hardcoded to 'v4'.
/// </summary>
public class IdEntry : EnrContentEntry<string>
{
    private IdEntry() : base("v4") { }

    public static IdEntry Instance { get; } = new();

    public override string Key => EnrContentKey.Id;

    protected override int GetRlpLengthOfValue()
    {
        return Rlp.LengthOf(Value);
    }

    protected override void EncodeValue(RlpStream rlpStream)
    {
        rlpStream.Encode("v4");
    }
}
