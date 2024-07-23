// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;

namespace Nethermind.Verkle.Tree;
#pragma warning disable 0649
public class VerkleTreeStats
{
    private const int Levels = 128;
    internal readonly int[] _stateLevels = new int[Levels];
    internal long _codeSize;
    internal int _missingLeaf;
    internal int _stateBranchCount;
    internal int _stateLeafCount;
    internal long _stateSize;
    internal int _stateStemCount;

    public int StateBranchCount => _stateBranchCount;

    public int StateStemCount => _stateStemCount;

    public int StateLeafCount => _stateLeafCount;

    public int MissingLeaf => _missingLeaf;

    public int StateCount => StateLeafCount + StateStemCount + StateBranchCount;
    public long CodeSize => _codeSize;

    public long StateSize => _stateSize;

    public long Size => StateSize + CodeSize;

    public int[] StateLevels => _stateLevels;

    public int[] AllLevels
    {
        get
        {
            var result = new int[Levels];
            for (var i = 0; i < result.Length; i++) result[i] = _stateLevels[i];

            return result;
        }
    }

    public override string ToString()
    {
        StringBuilder builder = new();
        builder.AppendLine("TRIE STATS");
        builder.AppendLine($"  SIZE {Size} (STATE {StateSize}, CODE {CodeSize})");
        builder.AppendLine($"  STATE NODES {StateCount} ({StateBranchCount}|{StateStemCount}|{StateLeafCount})");
        builder.AppendLine($"  MISSING {MissingLeaf}");
        builder.AppendLine($"  ALL LEVELS {string.Join(" | ", AllLevels)}");
        builder.AppendLine($"  STATE LEVELS {string.Join(" | ", StateLevels)}");
        return builder.ToString();
    }
}
#pragma warning restore 0649
