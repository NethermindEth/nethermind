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

using System;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Network.Enr.Test;

[TestFixture]
public class NodeRecordTests
{
    [Test]
    public void Get_value_or_obj_can_return_when_not_null()
    {
        NodeRecord nodeRecord = new ();
        nodeRecord.SetEntry(new UdpEntry(12345));
        nodeRecord.SetEntry(new Secp256K1Entry(
            new CompressedPublicKey(new byte[33])));
        nodeRecord.GetValue<int>(EnrContentKey.Udp).Should().Be(12345);
        nodeRecord.GetObj<CompressedPublicKey>(EnrContentKey.Secp256K1).Should().Be(
            new CompressedPublicKey(new byte[33]));
    }
    
    [Test]
    public void Get_value_or_obj_can_handle_missing_values()
    {
        NodeRecord nodeRecord = new ();
        nodeRecord.GetValue<int>(EnrContentKey.Udp).Should().BeNull();
        nodeRecord.GetObj<CompressedPublicKey>(EnrContentKey.Secp256K1).Should().BeNull();
    }
    
    [Test]
    public void Cannot_get_enr_string_when_signature_missing()
    {
        NodeRecord nodeRecord = new ();
        Assert.Throws<Exception>(() => _ = nodeRecord.EnrString);
    }

    [Test]
    public void Enr_content_entry_has_hash_code()
    {
        EnrContentEntry a = IdEntry.Instance;
        _ = a.GetHashCode();
    }
    
    [Test]
    public void Enr_content_entry_has_to_string()
    {
        EnrContentEntry a = IdEntry.Instance;
        _ = a.ToString();
    }
}
