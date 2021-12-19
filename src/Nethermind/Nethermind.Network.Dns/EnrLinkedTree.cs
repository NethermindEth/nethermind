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

namespace Nethermind.Network.Dns;

/// <summary>
/// enr:[node-record] is a leaf containing a node record. The node record is encoded as a URL-safe base64 string.
/// Note that this type of entry matches the canonical ENR text encoding. It may only appear in the enr-root subtree.
/// </summary>
public class EnrLinkedTree : EnrTreeNode
{
    public string Link { get; set; }

    public override string ToString()
    {
        return $"enrtree://{Link}";
    }

    public override string[] Links => new[] { Link };
    
    public override string[] Refs => Array.Empty<string>();

    public override string[] Records => Array.Empty<string>();
}
