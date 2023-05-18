// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Evm.EOF;


//  A better refactoring would be to remove all path that handles CALLF, and extract subgraphs that are result of only JUMPF calls
internal class EofCallGraph
{
    private HashSet<ushort> _nonReturningFunctionIds = new();
    private Dictionary<ushort, HashSet<(Instruction, ushort)>> _edges = new();


    public void FlagSection(ushort section, bool isNonReturning)
    {
        if (isNonReturning)
        {
            _nonReturningFunctionIds.Add(section);
        }
        else
        {
            _nonReturningFunctionIds.Remove(section);
        }
    }

    public void AddCall(ushort from, ushort to, Instruction callType)
    {
        if (!_edges.ContainsKey(from))
        {
            _edges[from] = new() { (callType, to) };
        }
        else
        {
            _edges[from].Add((callType, to));
        }
    }
    public bool TraverseAndValidate(ushort from)
    {
        HashSet<ushort> _memoization = new();

        bool TraverseLoop(ushort start, Predicate<ushort> check = null)
        {
            if (!_edges.ContainsKey(start))
                return true;

            _memoization.Add(start);
            foreach (var (callType, target) in _edges[start])
            {
                if (check?.Invoke(target) ?? true)
                {
                    if (_nonReturningFunctionIds.Contains(target))
                    {
                        return TraverseLoop(target, callType is Instruction.CALLF ? null : codeSection => _nonReturningFunctionIds.Contains(codeSection));
                    }
                    else
                    {
                        return TraverseLoop(target);
                    }
                }
                else
                {
                    return false;
                }
            }
            return true;
        }

        return TraverseLoop(from);
    }
}
