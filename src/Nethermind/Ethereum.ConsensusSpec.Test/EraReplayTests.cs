// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using Ethereum.Ssz.Test;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Legacy;
using Nethermind.BeaconChain.Legacy.Era;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Types;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Db;
using Nethermind.Logging;
using Snappier;

namespace Ethereum.ConsensusSpec.Test;

[TestFixture]
public class EraReplayTests
{
    private const ushort Version = 0x3265;
    private const ushort Block = 0x0001;
    private const ushort State = 0x0002;
    private const ushort SlotIndex = 0x3269;

    private DirectoryInfo _directory = null!;

    [SetUp]
    public void SetUp() => _directory = Directory.CreateTempSubdirectory();

    [TearDown]
    public void TearDown() => _directory.Delete(recursive: true);

    [Test]
    public void Reads_the_blocks_and_the_state_of_an_era()
    {
        byte[][] blocks = [[1, 2, 3], [], [4]];
        byte[] state = [5, 6, 7, 8];

        using EraFileReader reader = new(WriteEra(1, state, blocks));

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(reader.ReadState(), Is.EqualTo(state));
        Assert.That(reader.ReadBlocks(), Is.EqualTo(blocks));
        Assert.That(reader.ReadState(), Is.EqualTo(state), "the state can be read after the blocks");
    }

    private static IEnumerable<TestCaseData> MalformedFiles()
    {
        byte[] valid = EraBytes([5, 6, 7, 8], [[1, 2, 3]]);
        yield return new TestCaseData(Array.Empty<byte>()).SetName("Empty file is refused");
        yield return new TestCaseData(valid[..^1]).SetName("File without its last byte is refused");
        yield return new TestCaseData(valid[..40]).SetName("File that ends inside an entry is refused");
        yield return new TestCaseData(Patched(valid, valid.Length - 32, [0x02])).SetName("Another entry than a state index at the end is refused");
        yield return new TestCaseData(Patched(valid, valid.Length - 16, [1, 0, 0, 0, 0, 0, 0, 0])).SetName("State offset past the index is refused");
        yield return new TestCaseData(Patched(valid, valid.Length - 16, [0, 0, 0, 0, 0, 0, 0, 0x80])).SetName("State offset before the file is refused");
        yield return new TestCaseData(Patched(valid, valid.Length - 16, BitConverter.GetBytes(-(long)(valid.Length - 32)))).SetName("State offset of another entry is refused");
        yield return new TestCaseData(Patched(valid, 10, [0xff, 0xff, 0xff, 0x7f])).SetName("Entry length past the file is refused");
    }

    [TestCaseSource(nameof(MalformedFiles))]
    public void Malformed_file_is_refused(byte[] contents)
    {
        string path = Path.Combine(_directory.FullName, "malformed.era");
        File.WriteAllBytes(path, contents);
        using EraFileReader reader = new(path);

        Assert.That(() =>
        {
            foreach (byte[] _ in reader.ReadBlocks()) { }
            reader.ReadState();
        }, Throws.TypeOf<InvalidDataException>());
    }

    [TestCase(null, null, TestName = "Eras that form a chain are accepted")]
    [TestCase("genesis", "starts from state", TestName = "Chain from another genesis state is refused")]
    [TestCase("link", "Era 2 starts from state", TestName = "Era that starts from another state is refused")]
    [TestCase("missing", "file of era 1 is missing", TestName = "Missing era is refused")]
    [TestCase("block", "Era 2 is invalid", TestName = "Invalid block is refused")]
    public void Replays_only_one_chain_from_genesis(string? fault, string? message)
    {
#if MINIMAL_PRESET
        BeaconChainSpec spec = Phase0Spec;
        (byte[][] states, Hash256 genesisRoot) = EmptyEras(spec, lastEra: 3);
        for (int era = 0; era < states.Length; era++)
            WriteEra(era, states[era], fault == "block" && era == 2 ? [new byte[200]] : []);
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), spec);
        void Replay(ulong endEra) =>
            new EraReplayer(spec, store, new EraReplayConfig { Directory = _directory.FullName, EndEra = endEra, Lanes = 2 }, LimboLogs.Instance).Run(genesisRoot, default);

        switch (fault)
        {
            case "genesis":
                genesisRoot = Hash256.Zero;
                break;
            case "link":
                // A later run starts a new lane from the file's state.
                Replay(1);
                byte[] otherState = (byte[])states[1].Clone();
                otherState[0] ^= 1;
                WriteEra(1, otherState, []);
                break;
            case "missing":
                File.Delete(EraPath(1));
                break;
        }
        if (message is not null)
        {
            Assert.That(() => Replay(3), Throws.Exception.Matches<Exception>(e => e is InvalidDataException or InvalidConfigurationException or AggregateException)
                .And.Matches<Exception>(e => $"{e}".Contains(message, StringComparison.Ordinal)));
            return;
        }

        Replay(1);
        Assert.That(() => Replay(3), Throws.Nothing, "a later run continues where the first ended");
        WriteEra(2, states[2], [new byte[200]]);
        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(() => Replay(3), Throws.Nothing, "a recorded era is not replayed again");
        Assert.That(store.TryGetAnchor(out _, out _), Is.False, "only a replay that reaches Fulu stores an anchor");
#else
        Assert.Ignore("Only the minimal preset has eras short enough to replay here.");
#endif
    }

    /// <summary>Replays real eras against their files' end states, for example the first era after each fork boundary.</summary>
    [Explicit("Needs mainnet era files in NETHERMIND_ERA_DIRECTORY")]
    [TestCase(1)]
    [TestCase(291)]
    [TestCase(567)]
    [TestCase(759)]
    [TestCase(1054)]
    [TestCase(1423)]
    [TestCase(1608)]
    public void Replays_mainnet_era(int era)
    {
        string directory = Environment.GetEnvironmentVariable("NETHERMIND_ERA_DIRECTORY")!;
        string Find(int number) => Directory.GetFiles(directory, $"mainnet-{number:D5}-*.era").Single();
        LegacyChain Load(int number, bool verifySignatures)
        {
            using EraFileReader reader = new(Find(number));
            return new LegacyChain(BeaconChainSpec.Mainnet, reader.ReadState(), new ForkChoiceStepDriver.FixedNewPayloadNotifier(ExecutionStatus.Optimistic), verifySignatures);
        }

        LegacyChain chain = Load(era - 1, verifySignatures: true);
        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
        using EraFileReader blocks = new(Find(era));
        int count = 0;
        foreach (byte[] block in blocks.ReadBlocks())
        {
            chain.ApplyBlock(block);
            count++;
        }
        chain.AdvanceTo((ulong)era * Presets.SlotsPerHistoricalRoot);
        using EraFileReader next = new(Find(era + 1));
        byte[] boundaryBlock = next.ReadBlocks().First();
        if (SignedBeaconBlockCodec.TryReadSlot(boundaryBlock, out ulong slot) && slot == chain.State.Slot)
            chain.ApplyBlock(boundaryBlock);

        TestContext.Out.WriteLine($"era {era}: {count} blocks, {chain.State.Validators!.Length} validators, {stopwatch.Elapsed.TotalSeconds:F0} s");
        Assert.That(chain.ComputeStateRoot(), Is.EqualTo(Load(era, verifySignatures: false).ComputeStateRoot()));
    }

#if MINIMAL_PRESET
    private static BeaconChainSpec Phase0Spec => FuluDriverSupport.DefaultSpec with
    {
        Forks = [.. FuluDriverSupport.DefaultSpec.Forks.Take(7).Select((entry, i) => entry with { Epoch = i == 0 ? 0 : Presets.FarFutureEpoch })],
        ElectraForkEpoch = Presets.FarFutureEpoch,
        FuluForkEpoch = Presets.FarFutureEpoch,
        GloasForkEpoch = Presets.FarFutureEpoch,
    };

    private static (byte[][] States, Hash256 GenesisRoot) EmptyEras(BeaconChainSpec spec, int lastEra)
    {
        byte[] genesis = SszConsensusTestLoader.ReadSszSnappy(Path.Combine(ConsensusSpecArchive.SuitePath(ConsensusPreset.Minimal, "phase0", "sanity")!, "slots", "pyspec_tests", "slots_1", "pre.ssz_snappy"));
        LegacyChain chain = new(spec, genesis, new ForkChoiceStepDriver.FixedNewPayloadNotifier(ExecutionStatus.Optimistic), verifySignatures: false);
        Hash256 genesisRoot = chain.ComputeStateRoot();
        byte[][] states = new byte[lastEra + 1][];
        states[0] = genesis;
        for (int era = 1; era <= lastEra; era++)
        {
            chain.AdvanceTo((ulong)era * Presets.SlotsPerHistoricalRoot);
            states[era] = Nethermind.BeaconChain.Legacy.Phase0.BeaconState.Encode((Nethermind.BeaconChain.Legacy.Phase0.BeaconState)chain.State);
        }
        return (states, genesisRoot);
    }
#endif

    private string EraPath(int era) => Path.Combine(_directory.FullName, $"test-{era:D5}-00000000.era");

    private string WriteEra(int era, byte[] state, byte[][] blocks)
    {
        File.WriteAllBytes(EraPath(era), EraBytes(state, blocks));
        return EraPath(era);
    }

    private static byte[] EraBytes(byte[] state, byte[][] blocks)
    {
        using MemoryStream file = new();
        WriteEntry(file, Version, []);
        foreach (byte[] block in blocks)
            WriteEntry(file, Block, Compress(block));
        long stateOffset = file.Position;
        WriteEntry(file, State, Compress(state));

        byte[] index = new byte[24];
        BinaryPrimitives.WriteInt64LittleEndian(index.AsSpan(8), stateOffset - file.Position);
        BinaryPrimitives.WriteInt64LittleEndian(index.AsSpan(16), 1);
        WriteEntry(file, SlotIndex, index);
        return file.ToArray();
    }

    private static void WriteEntry(Stream file, ushort type, byte[] data)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(header, type);
        BinaryPrimitives.WriteUInt32LittleEndian(header[2..], (uint)data.Length);
        header[6..].Clear();
        file.Write(header);
        file.Write(data);
    }

    private static byte[] Compress(byte[] data)
    {
        using MemoryStream compressed = new();
        using (SnappyStream stream = new(compressed, CompressionMode.Compress, leaveOpen: true))
            stream.Write(data);
        return compressed.ToArray();
    }

    private static byte[] Patched(byte[] data, int offset, byte[] patch)
    {
        byte[] patched = (byte[])data.Clone();
        patch.CopyTo(patched, offset);
        return patched;
    }
}
