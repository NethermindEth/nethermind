// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Core.ZkEvm.Test.Extensions;

/// <summary>
/// Resolves every method the ZisK guest's substitutions.xml names. ILC applies a substitution only to a
/// method it finds and otherwise keeps the original body, so a renamed member or a stale signature would
/// quietly undo the substitution - <c>ZkEvmBitOperations.HasByteReverse</c> would stay false and ZisK would
/// go back to the mask form without any build noticing.
/// </summary>
public class GuestSubstitutionsTests
{
    private const BindingFlags AnyMethod =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

    private static readonly Dictionary<string, Assembly> Assemblies = new()
    {
        ["System.Private.CoreLib"] = typeof(object).Assembly,
        ["Nethermind.Core"] = typeof(ZkEvmBitOperations).Assembly,
    };

    private static IEnumerable<TestCaseData> SubstitutedMethods()
    {
        XDocument document = XDocument.Load(Path.Combine(TestContext.CurrentContext.TestDirectory, "ZiskGuest.substitutions.xml"));
        foreach (XElement assembly in document.Root!.Elements("assembly"))
        {
            foreach (XElement type in assembly.Elements("type"))
            {
                foreach (XElement method in type.Elements("method"))
                {
                    string assemblyName = (string)assembly.Attribute("fullname")!;
                    string typeName = (string)type.Attribute("fullname")!;
                    string signature = (string)method.Attribute("signature")!;
                    yield return new TestCaseData(assemblyName, typeName, signature).SetName($"{typeName} {signature}");
                }
            }
        }
    }

    [TestCaseSource(nameof(SubstitutedMethods))]
    public void Substitution_names_an_existing_method(string assemblyName, string typeName, string signature)
    {
        Assert.That(Assemblies.TryGetValue(assemblyName, out Assembly? assembly), Is.True,
            $"{assemblyName} is not mapped to a loaded assembly; add it to {nameof(Assemblies)}");

        Type? type = assembly!.GetType(typeName);
        Assert.That(type, Is.Not.Null, $"{typeName} is not in {assemblyName}");

        // "ReturnType Name(ParamType,ParamType)", with full type names, as ILLink writes them.
        int space = signature.IndexOf(' ');
        int open = signature.IndexOf('(');
        string returnType = signature[..space];
        string name = signature[(space + 1)..open];
        string[] parameters = signature[(open + 1)..^1].Split(',', StringSplitOptions.RemoveEmptyEntries);

        bool found = type!.GetMethods(AnyMethod).Any(m =>
            m.Name == name
            && m.ReturnType.FullName == returnType
            && m.GetParameters().Select(p => p.ParameterType.FullName).SequenceEqual(parameters));

        Assert.That(found, Is.True, $"{typeName} has no method {signature}");
    }
}
