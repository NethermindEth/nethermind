// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;

namespace Nethermind.DocGen.Test;

public class DbSizeGeneratorTests
{
    [TestCase("mainnet.txt")]
    [TestCase("README.txt")]
    public void Only_json_reports_generate_chain_tabs(string otherFile)
    {
        DirectoryInfo temp = Directory.CreateTempSubdirectory("nethermind-docgen-");
        try
        {
            string source = temp.CreateSubdirectory("reports").FullName;
            File.WriteAllText(Path.Combine(source, "mainnet.json"), """{"state":"1G","nethermind_db":"2G"}""");
            File.WriteAllText(Path.Combine(source, otherFile), "Size report notes");
            File.WriteAllText(Path.Combine(temp.FullName, "versions.json"), """["2.3.0"]""");

            string current = Path.Combine(temp.CreateSubdirectory("docs/fundamentals").FullName, "database.md");
            string versioned = Path.Combine(temp.CreateSubdirectory("versioned_docs/version-2.3.0/fundamentals").FullName, "database.md");
            const string template = "Intro\n<!--[start autogen]-->\nOld sizes\n<!--[end autogen]-->\n";
            File.WriteAllText(current, template);
            File.WriteAllText(versioned, template);

            DBSizeGenerator.Generate(temp.FullName, source);

            using (Assert.EnterMultipleScope())
            {
                foreach (string page in new[] { current, versioned })
                {
                    string[] tabs = [.. File.ReadLines(page).Where(line => line.StartsWith("<TabItem", StringComparison.Ordinal))];
                    Assert.That(tabs, Is.EqualTo(new[] { "<TabItem value=\"mainnet\" label=\"Mainnet\">" }), page);
                    Assert.That(File.ReadAllText(page), Does.Contain("**Total: 2 GB**"), page);
                }
            }
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }
}
