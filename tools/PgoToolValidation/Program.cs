// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: PgoToolValidation <dotnet-pgo-directory> <mibc-parser-fixture>");
    return 2;
}

string toolDirectory = Path.GetFullPath(args[0]);
AssemblyLoadContext.Default.Resolving += (_, name) => Assembly.LoadFrom(Path.Combine(toolDirectory, name.Name + ".dll"));
Assembly tool = Assembly.LoadFrom(Path.Combine(toolDirectory, "dotnet-pgo.dll"));
using ZipArchive archive = ZipFile.OpenRead(args[1]);
using MemoryStream peStream = new();
using Stream entryStream = archive.Entries.Single(entry => entry.Name.EndsWith(".dll", StringComparison.Ordinal)).Open();
entryStream.CopyTo(peStream);
peStream.Position = 0;
using PEReader pe = new(peStream);
Type contextType = tool.GetType("Microsoft.Diagnostics.Tools.Pgo.TypeRefTypeSystem.TypeRefTypeSystemContext", true);
object context = Activator.CreateInstance(contextType, new object[] { new[] { pe } });
Type parser = tool.GetType("ILCompiler.IBC.MIbcProfileParser", true);
MethodInfo parse = parser.GetMethod("ParseMIbcFile");
object profileData = parse.Invoke(null, new object[] { context, pe, null, null, Type.Missing, Type.Missing });
IEnumerable records = (IEnumerable)profileData.GetType().GetMethod("GetAllMethodProfileData").Invoke(profileData, null);
object[] keys = records.Cast<object>().Take(2).Select(record => record.GetType().GetField("Method").GetValue(record)).ToArray();
Type blockType = tool.GetType("Internal.IL.BasicBlock", true);
Type graphType = tool.GetType("Internal.IL.FlowGraph", true);
Type sampleType = tool.GetType("Microsoft.Diagnostics.Tools.Pgo.SampleProfile", true);
Type correlatorType = tool.GetType("Microsoft.Diagnostics.Tools.Pgo.SampleCorrelator", true);
Type infoType = correlatorType.GetNestedType("PerMethodInfo", BindingFlags.NonPublic);

object MakeProfile(bool disconnected)
{
    IList blocks = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(blockType));
    object start = Activator.CreateInstance(blockType, new object[] { 0, 1 });
    blocks.Add(start);
    object sampled = start;
    if (disconnected)
    {
        sampled = Activator.CreateInstance(blockType, new object[] { 1, 1 });
        object targets = blockType.GetProperty("Targets").GetValue(sampled);
        targets.GetType().GetMethod("Add").Invoke(targets, new[] { sampled });
        blocks.Add(sampled);
    }
    object graph = graphType.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single().Invoke(new object[] { blocks });
    object profile = Activator.CreateInstance(sampleType, new[] { null, graph });
    sampleType.GetMethod("AttributeSamples").Invoke(profile, new object[] { sampled, 10L });
    return profile;
}

object MakeCorrelator(object badProfile, out IDictionary dictionary)
{
    object correlator = Activator.CreateInstance(correlatorType, new object[] { null });
    dictionary = (IDictionary)correlatorType.GetField("_methodInf", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(correlator);
    foreach ((object key, object profile) in new[] { (keys[0], badProfile), (keys[1], MakeProfile(false)) })
    {
        object info = Activator.CreateInstance(infoType, true);
        infoType.GetProperty("Profile").SetValue(info, profile);
        dictionary.Add(key, info);
    }
    return correlator;
}

MethodInfo smooth = correlatorType.GetMethod("SmoothAllProfiles");
foreach (bool logging in new[] { true, false })
{
    object correlator = MakeCorrelator(MakeProfile(true), out IDictionary dictionary);
    List<string> warnings = [];
    Action<string> logger = logging ? warnings.Add : null;
    try
    {
        smooth.Invoke(correlator, smooth.GetParameters().Length == 0 ? null : new object[] { logger });
    }
    catch (TargetInvocationException exception)
    {
        Console.WriteLine($"Smoothing propagated {exception.InnerException.GetType().Name}: {exception.InnerException.Message}");
        return 1;
    }
    if (dictionary.Contains(keys[0]) || !dictionary.Contains(keys[1]) || warnings.Count != (logging ? 1 : 0))
        throw new Exception("Invalid profile retention or warning behavior");
    object good = correlatorType.GetMethod("GetProfile").Invoke(correlator, new[] { keys[1] });
    if (sampleType.GetProperty("SmoothedSamples").GetValue(good) == null)
        throw new Exception("Valid method was not smoothed");
    Console.WriteLine($"PASS: failed profile removed; valid profile smoothed; logging={logging}; warnings={warnings.Count}");
}
object unrelated = MakeCorrelator(null, out IDictionary untouched);
try
{
    smooth.Invoke(unrelated, new object[] { null });
    throw new Exception("Unrelated failure was swallowed");
}
catch (TargetInvocationException exception) when (exception.InnerException is NullReferenceException)
{
    if (!untouched.Contains(keys[0]))
        throw new Exception("Unrelated failure removed a method");
    Console.WriteLine("PASS: unrelated failure propagates");
}
Type memoryMapType = tool.GetType("Microsoft.Diagnostics.Tools.Pgo.MethodMemoryMap", true);
Type regionType = tool.GetType("Microsoft.Diagnostics.Tools.Pgo.MemoryRegionInfo", true);
Type mappingType = tool.GetType("Microsoft.Diagnostics.Tools.Pgo.IPMapping", true);
Type keyValueMapType = tool.GetType("Microsoft.Diagnostics.Tools.Pgo.KeyValueMap`2", true).MakeGenericType(typeof(uint), mappingType);
object mapping = Activator.CreateInstance(mappingType, new object[] { 0, null, keys[0] });
Array mappings = Array.CreateInstance(mappingType, 1);
mappings.SetValue(mapping, 0);
object nativeToIl = Activator.CreateInstance(keyValueMapType, new object[] { new uint[] { 0 }, mappings });
object region = Activator.CreateInstance(regionType);
regionType.GetProperty("StartAddress").SetValue(region, 100UL);
regionType.GetProperty("EndAddress").SetValue(region, 110UL);
regionType.GetProperty("Method").SetValue(region, keys[0]);
regionType.GetProperty("NativeToILMap").SetValue(region, nativeToIl);
Array regions = Array.CreateInstance(regionType, 1);
regions.SetValue(region, 0);
object memoryMap = RuntimeHelpers.GetUninitializedObject(memoryMapType);
memoryMapType.GetField("_infoKeys", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(memoryMap, new ulong[] { 100 });
memoryMapType.GetField("_infos").SetValue(memoryMap, regions);
object weightedCorrelator = Activator.CreateInstance(correlatorType, new[] { memoryMap });
IDictionary weightedMethods = (IDictionary)correlatorType.GetField("_methodInf", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(weightedCorrelator);
object weightedProfile = MakeProfile(false);
object weightedInfo = Activator.CreateInstance(infoType, true);
infoType.GetProperty("Profile").SetValue(weightedInfo, weightedProfile);
weightedMethods.Add(keys[0], weightedInfo);
MethodInfo attribute = correlatorType.GetMethod("AttributeSamplesToIP");
long totalWeight = 0;
foreach (long weight in new[] { 0L, 1L, 2L, 3_000_000_000L })
{
    attribute.Invoke(weightedCorrelator, new object[] { 100UL, weight });
    totalWeight += weight;
    long attributed = (long)correlatorType.GetProperty("TotalAttributedSamples").GetValue(weightedCorrelator);
    long profileWeight = (long)sampleType.GetProperty("AttributedSamples").GetValue(weightedProfile);
    IDictionary raw = (IDictionary)sampleType.GetProperty("RawSamples").GetValue(weightedProfile);
    if (attributed != totalWeight || profileWeight != 10 + totalWeight || raw.Values.Cast<long>().Single() != 10 + totalWeight)
        throw new Exception($"Sample weight {weight} was not preserved in both diagnostics and block counts");
}
attribute.Invoke(weightedCorrelator, new object[] { 110UL, 7L });
if ((long)correlatorType.GetProperty("SamplesOutsideManagedCode").GetValue(weightedCorrelator) != 7 ||
    (long)sampleType.GetProperty("AttributedSamples").GetValue(weightedProfile) != 10 + totalWeight)
    throw new Exception("Outside-code samples changed the managed profile");
Console.WriteLine("PASS: zero, unit, non-unit and 64-bit sample weights reach raw block counts; outside-code samples stay separate");
return 0;
