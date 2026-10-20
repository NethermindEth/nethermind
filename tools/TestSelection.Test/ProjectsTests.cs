// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.TestSelection.Selection;
using NUnit.Framework;

namespace Nethermind.TestSelection.Test;

[TestFixture]
public class ProjectsTests
{
    [Test]
    public void Changed_FilesInProjects_MapToTheirProjects()
    {
        Change change = Fixtures.Projects.Changed([$"{Fixtures.Evm}/VirtualMachine.cs", $"{Fixtures.EvmTest}/OpcodeTests.cs"]);
        Assert.That(change.Projects, Is.EquivalentTo(new[] { Fixtures.Evm, Fixtures.EvmTest }), "each file belongs to its project");
    }

    [Test]
    public void Changed_FilesNoTestBuildsOrRunsFrom_ChangeNothing()
    {
        string[] files =
        [
            "README.md", "tools/DocGen/x.cs", ".github/workflows/rpc-bench.yml", "Dockerfile", "cspell.json",
            ".github/actions/gcp-runner/action.yml", "nix/nuget-deps.json", "scripts/rpc-bench/run.py", $"{Projects.SourceRoot}/Nethermind.slnx",
        ];
        Change change = Fixtures.Projects.Changed(files);
        Assert.That(change.TouchesEverything || change.Projects.Count > 0, Is.False, "none of these can change a test result");
    }

    [TestCase("src/Nethermind/Directory.Build.props", TestName = "SourceBuildProps")]
    [TestCase("Directory.Packages.props", TestName = "Packages")]
    [TestCase("global.json", TestName = "Sdk")]
    [TestCase("*", TestName = "DiffTooLargeToList")]
    [TestCase(".github/workflows/run-nethtest.yml", TestName = "EestWorkflow")]
    [TestCase(".github/workflows/nethermind-tests-trie.yml", TestName = "VariantWorkflow")]
    [TestCase(".github/actions/select-tests/action.yml", TestName = "SelectionAction")]
    [TestCase("scripts/ci/test-matrices.json", TestName = "Matrices")]
    [TestCase("tools/TestSelection/Program.cs", TestName = "Selector")]
    [TestCase("src/tests/BlockchainTests/a.json", TestName = "FixtureSubmodule")]
    public void Changed_FileEveryTestDependsOn_TouchesEverything(string file) =>
        Assert.That(Fixtures.Projects.Changed([file]).TouchesEverything, Is.True, "every test builds or runs from this file");

    [Test]
    public void Changed_PathAProjectIncludes_BelongsToThatProject() =>
        Assert.That(Fixtures.Projects.Changed([$"{Projects.SourceRoot}/Chains/foundation.json"]).Projects, Is.EquivalentTo(new[] { Fixtures.Config }),
            "Config embeds the chain specs");

    [Test]
    public void Changed_NewProject_BelongsToItself() =>
        Assert.That(Fixtures.Projects.Changed([$"{Projects.SourceRoot}/Nethermind.New/A.cs"]).Projects, Is.EquivalentTo(new[] { Projects.DirectoryOf("Nethermind.New") }),
            "whatever uses the new project changes its references too");

    [Test]
    public void DirectTests_AreChangedTestProjectsAndThoseReferencingAChangedProject()
    {
        Assert.That(Fixtures.Projects.DirectTests(new HashSet<string> { Fixtures.Evm }), Is.EquivalentTo(new[] { "Nethermind.Evm.Test" }),
            "JsonRpc.Test references JsonRpc only");
        Assert.That(Fixtures.Projects.DirectTests(new HashSet<string> { Fixtures.JsonRpcTest }), Is.EquivalentTo(new[] { "Nethermind.JsonRpc.Test" }),
            "a changed test project runs");
    }

    [Test]
    public void Dependencies_FollowReferencesTransitively() =>
        Assert.That(Fixtures.Projects.Dependencies(Fixtures.EvmTest), Is.EquivalentTo(new[] { Fixtures.Evm, Fixtures.Core }), "Evm.Test reaches Core through Evm");

    [Test]
    public void OfRepository_ReadsReferencesAndIncludesFromCsprojFiles()
    {
        using TempDirectory root = new();
        WriteProject(root, "Nethermind.Core", string.Empty);
        WriteProject(root, "Nethermind.Config", """
            <EmbeddedResource Include="..\Chains\**\*.*" />
            <Content Include="..\..\tests\a.json" />
            <Compile Include="Local.cs" />
            <ProjectReference Include="..\Nethermind.Core\Nethermind.Core.csproj" />
            """);

        Projects projects = Projects.OfRepository(root.Path);

        Assert.That(projects.Dependencies(Fixtures.Config), Is.EquivalentTo(new[] { Fixtures.Core }), "the project reference is read");
        Assert.That(projects.Changed([$"{Projects.SourceRoot}/Chains/x.json"]).Projects, Is.EquivalentTo(new[] { Fixtures.Config }), "a wildcard include owns its directory");
        Assert.That(projects.Changed(["src/tests/a.json"]).Projects, Is.EquivalentTo(new[] { Fixtures.Config }), "an included file outside the sources is owned");
    }

    private static void WriteProject(TempDirectory root, string name, string items)
    {
        string directory = Path.Combine(root.Path, Projects.SourceRoot, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, $"{name}.csproj"), $"<Project><ItemGroup>{items}</ItemGroup></Project>");
    }
}
