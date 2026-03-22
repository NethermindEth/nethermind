// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
//
// Supplementary file compiled into dotnet-pgo to support Nethermind's PGO pipeline.
// Reads .spgo (leaf sample IPs) and .callgraph (caller-callee IP pairs) files
// produced by PgoTrim from perfcollect's perf.data.txt.
//
// Copied into the dotnet-pgo source tree at build time by collect-pgo-profile.yml.

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

using Internal.TypeSystem;

namespace Microsoft.Diagnostics.Tools.Pgo
{
    internal static class NethermindPgoPatches
    {
        /// <summary>
        /// Reads a .spgo file (one hex IP per line) and attributes each sample
        /// to the correlator for SPGO basic block attribution.
        /// </summary>
        public static void LoadSpgoSamples(string etlxPath, SampleCorrelator correlator)
        {
            string spgoFile = Path.ChangeExtension(etlxPath, ".spgo");
            if (!File.Exists(spgoFile))
                return;

            int count = 0;
            foreach (string line in File.ReadLines(spgoFile))
            {
                if (ulong.TryParse(line.AsSpan().Trim(), NumberStyles.HexNumber, null, out ulong ip))
                {
                    correlator.AttributeSamplesToIP(ip, 1);
                    count++;
                }
            }
            Program.PrintOutput($"Supplementary .spgo samples loaded: {count}");
        }

        /// <summary>
        /// Reads a .callgraph file (callee_hex_ip caller_hex_ip per line) and
        /// populates the call graph and exclusive sample counts for Pettis-Hansen.
        /// </summary>
        public static void LoadCallGraph(
            string etlxPath,
            MethodMemoryMap mmap,
            Dictionary<MethodDesc, Dictionary<MethodDesc, int>> callGraph,
            Dictionary<MethodDesc, int> exclusiveSamples)
        {
            if (callGraph == null || exclusiveSamples == null || mmap == null)
                return;

            string callGraphFile = Path.ChangeExtension(etlxPath, ".callgraph");
            if (!File.Exists(callGraphFile))
                return;

            int edgeCount = 0;
            int sampleCount = 0;

            foreach (string line in File.ReadLines(callGraphFile))
            {
                ReadOnlySpan<char> span = line.AsSpan().Trim();
                if (span.IsEmpty || span[0] == '#')
                    continue;

                int space = span.IndexOf(' ');
                if (space <= 0)
                    continue;

                if (!ulong.TryParse(span.Slice(0, space), NumberStyles.HexNumber, null, out ulong calleeIp))
                    continue;
                if (!ulong.TryParse(span.Slice(space + 1), NumberStyles.HexNumber, null, out ulong callerIp))
                    continue;

                MethodDesc callee = mmap.GetMethod(calleeIp);
                MethodDesc caller = mmap.GetMethod(callerIp);

                // Count exclusive samples for the callee (leaf of stack)
                if (callee != null)
                {
                    sampleCount++;
                    if (exclusiveSamples.TryGetValue(callee, out int count))
                        exclusiveSamples[callee] = count + 1;
                    else
                        exclusiveSamples[callee] = 1;
                }

                // Add caller->callee edge for Pettis-Hansen
                if (callee != null && caller != null)
                {
                    edgeCount++;

                    if (!callGraph.TryGetValue(caller, out Dictionary<MethodDesc, int> innerDict))
                    {
                        innerDict = new Dictionary<MethodDesc, int>();
                        callGraph[caller] = innerDict;
                    }
                    if (innerDict.TryGetValue(callee, out int edgeWeight))
                        innerDict[callee] = edgeWeight + 1;
                    else
                        innerDict[callee] = 1;
                }
            }

            Program.PrintOutput($"Supplementary .callgraph loaded: {edgeCount:N0} edges, {sampleCount:N0} exclusive samples");
        }

        /// <summary>
        /// Wraps SmoothAllProfiles with per-method try-catch so one method
        /// with a disconnected flow graph doesn't kill the entire SPGO pass.
        /// FlowSmoothing.MakeGraphFeasible crashes with "Stack empty" for some methods.
        /// </summary>
        public static void SafeSmoothAllProfiles(SampleCorrelator correlator)
        {
            // Access internal _methodInf via reflection since SmoothAllProfiles
            // iterates it directly and we need per-method error handling.
            System.Reflection.FieldInfo field = typeof(SampleCorrelator).GetField("_methodInf",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (field == null || field.GetValue(correlator) is not System.Collections.IDictionary dict)
            {
                // Fallback: call original and let it throw if it throws
                correlator.SmoothAllProfiles();
                return;
            }

            foreach (System.Collections.DictionaryEntry entry in dict)
            {
                try
                {
                    // PerMethodInfo.Profile.SmoothFlow()
                    object pmi = entry.Value;
                    System.Reflection.PropertyInfo profileProp = pmi.GetType().GetProperty("Profile");
                    object profile = profileProp.GetValue(pmi);
                    System.Reflection.MethodInfo smoothMethod = profile.GetType().GetMethod("SmoothFlow");
                    smoothMethod.Invoke(profile, null);
                }
                catch (Exception ex)
                {
                    string innerMsg = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                    Console.Error.WriteLine($"Warning: SmoothFlow failed for method: {innerMsg}");
                }
            }
        }
    }
}
