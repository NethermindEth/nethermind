// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using NUnit.Framework;

namespace Nethermind.Evm.Lean.SimpleTransferCompletionExtractor.Test;

[TestFixture]
public sealed class StageBEffectiveBlockGasTests
{
    private const string Expression = "BlockGas > 0 || BlockStateGas > 0 ? BlockGas : SpentGas";
    private string _root = null!;
    private Dictionary<string, byte[]> _fresh = null!;

    [OneTimeSetUp]
    public void SetUp()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, Extractor.GasConsumedPath)))
            current = current.Parent;
        _root = current?.FullName ?? throw new AssertionException("Repository root not found.");
        _fresh = StageBEffectiveBlockGasExtractor.RenderArtifacts(_root, CompilerSources.Load(_root));
    }

    [Test]
    public void Stage_b_effective_gas_artifacts_are_current_and_exclude_stage_a()
    {
        string output = Path.Combine(_root, "tools/Evm/Lean/SimpleTransferCompletionExtractor/StageB/Leaf/Generated");
        Dictionary<string, byte[]> supplied = Directory.EnumerateFiles(output)
            .ToDictionary(static path => Path.GetFileName(path), File.ReadAllBytes, StringComparer.Ordinal);
        Assert.That(() => StageBEffectiveBlockGasExtractor.ValidateArtifacts(supplied, _fresh), Throws.Nothing);
        Assert.That(Encoding.UTF8.GetString(_fresh[StageBEffectiveBlockGasExtractor.LeanName]),
            Does.Not.Contain("import SimpleTransferCompletionExtractor.Generated"));
    }

    [TestCase("BlockGas > 0 && BlockStateGas > 0 ? BlockGas : SpentGas")]
    [TestCase("BlockGas > 0 ? BlockGas : SpentGas")]
    [TestCase("BlockGas >= 0 || BlockStateGas > 0 ? BlockGas : SpentGas")]
    [TestCase("BlockGas > 0 || BlockStateGas > 0 ? BlockStateGas : SpentGas")]
    [TestCase("BlockGas > 0 || BlockStateGas > 0 ? BlockGas : OperationGas")]
    [TestCase("BlockGas > 0 || BlockStateGas > 0 ? SpentGas : BlockGas")]
    [TestCase("BlockGas > 0 || BlockStateGas > 0 ? BlockGas + 1 : SpentGas")]
    public void Stage_b_effective_gas_rejects_compile_valid_property_mutations(string replacement)
    {
        string source = File.ReadAllText(Path.Combine(_root, Extractor.GasConsumedPath));
        Assert.That(source, Does.Contain(Expression));
        CompilerClosure? mutant = null;
        Assert.That(() => mutant = CompilerSources.Load(_root,
            new Dictionary<string, string> { [Extractor.GasConsumedPath] = source.Replace(Expression, replacement, StringComparison.Ordinal) }),
            Throws.Nothing, "The mutation must compile before source admission is tested.");
        Assert.That(() => StageBEffectiveBlockGasExtractor.RenderArtifacts(_root, mutant!), Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void Stage_b_effective_gas_rejects_artifact_drift([Values("ir", "lean", "source-hash", "roster")] string mutation)
    {
        Dictionary<string, byte[]> supplied = _fresh.ToDictionary(static item => item.Key, static item => item.Value.ToArray(), StringComparer.Ordinal);
        if (mutation == "roster") supplied.Add("unexpected.json", []);
        else
        {
            string name = mutation switch
            {
                "ir" => StageBEffectiveBlockGasExtractor.IrName,
                "lean" => StageBEffectiveBlockGasExtractor.LeanName,
                _ => StageBEffectiveBlockGasExtractor.ManifestName,
            };
            string content = Encoding.UTF8.GetString(supplied[name]);
            supplied[name] = Encoding.UTF8.GetBytes(mutation == "source-hash"
                ? content.Replace("1dc4e78d5a17056dcb00d496136a32a899245f3a7b3e5a9eef74c4760ef68686", new string('0', 64), StringComparison.Ordinal)
                : content + "\n");
            Assert.That(supplied[name], Is.Not.EqualTo(_fresh[name]), "The negative control must alter an artifact.");
        }
        Assert.That(() => StageBEffectiveBlockGasExtractor.ValidateArtifacts(supplied, _fresh), Throws.TypeOf<ExtractionException>());
    }
}
