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
/// The root of the tree is a TXT record with the following content:
/// enrtree-root:v1 e=[enr-root] l=[link-root] seq=[sequence-number] sig=[signature]
/// </summary>
public class EnrTreeRoot : EnrTreeNode
{
    /// <summary>
    /// the root hashes of subtrees containing nodes and links subtrees
    /// </summary>
    public string EnrRoot { get; set; }

    /// <summary>
    /// the root hashes of subtrees containing nodes and links subtrees
    /// </summary>
    public string LinkRoot { get; set; }

    /// <summary>
    /// Updated each time the tree gets updated.
    /// </summary>
    public int Sequence { get; set; }

    /// <summary>
    /// Signature but need to learn where to take the public key from
    /// </summary>
    public string Signature { get; set; }

    public override string ToString()
    {
        return $"enrtree-root:v1 e={EnrRoot} l={LinkRoot} seq={Sequence} sig={Signature}";
    }

    public override string[] Refs
    {
        get
        {
            return new[] { EnrRoot, LinkRoot };
        }
    }

    public override string[] Links => Array.Empty<string>();
    
    public override string[] Records => Array.Empty<string>();
}
