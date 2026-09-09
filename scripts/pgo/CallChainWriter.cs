// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Internal.TypeSystem;

namespace Microsoft.Diagnostics.Tools.Pgo;

internal static class CallChainWriter
{
    internal static void Write(string output, Dictionary<MethodDesc, Dictionary<MethodDesc, int>> graph)
    {
        Dictionary<string, object[]> result = new();
        int retained = 0;
        foreach (KeyValuePair<MethodDesc, Dictionary<MethodDesc, int>> caller in graph)
        {
            string callerName = Name(caller.Key);
            if (callerName == null) continue;
            List<string> callees = new();
            List<int> weights = new();
            foreach (KeyValuePair<MethodDesc, int> callee in caller.Value)
            {
                string calleeName = Name(callee.Key);
                if (calleeName == null) continue;
                callees.Add(calleeName);
                weights.Add(callee.Value);
                retained++;
            }
            if (callees.Count != 0) result.Add(callerName, new object[] { callees, weights });
        }
        File.WriteAllText(output, JsonSerializer.Serialize(result));
        Program.PrintOutput($"CallFrequency edges retained: {retained}; excluded: {graph.Values.Sum(edges => edges.Count) - retained}");
    }

    private static string Name(MethodDesc method)
    {
        // CallChainProfile has no signature matching. Keep the complete, typed graph in
        // MIBC; export only names which cannot select a different overload or instantiation.
        if (method.HasInstantiation || method.OwningType is not MetadataType type || type.HasInstantiation ||
            type.ContainingType != null || method.Name.Contains('.') || method.Name.Contains('!') ||
            type.GetMethods().Count(candidate => candidate.Name == method.Name) != 1)
            return null;
        return type.Module.Assembly.GetName().Name + ".dll!" + type.GetFullName() + "." + method.Name;
    }
}
