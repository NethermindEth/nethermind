using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Fields.FrEElement;
using Nethermind.Verkle.Polynomial;
using Nethermind.Verkle.Proofs;

namespace Nethermind.Verkle.Tests.Proofs;

public class MultiProofTests
{
    private readonly string[] _basicProofStruct =
    {
        "1BD427779680F60C2DF48A9DF458FA989FC0F8A7A38D54363B722138E4E4AC43",
        "235B4ACC8ACE6A0D8FC3FB9142B69B2B989F97CE36BA43868D93ADD3ABE7A012",
        "51C5A0AA5CC5E53B697D8B57EAA43DB3DC3987F9F1E71C31B5098721DAD29104",
        "65FFF50D7FB4D0145C41C53A395F99052A251FCB97EF31DA582938A677566970",
        "24068F61BD61A10A2C7D8D2A522FA3834E1516F16BFC4EC7F1808069EFFEAB09",
        "5A5FF89D9BACAD138316AA7C9001CE03830E443DA2AED1F66B5211AE7912BBE7",
        "51BB05960D4F6BCDB3D266685D6E1B81C632E66F90DF80B76CFE8E619BB29ED3",
        "322C2F9743D918F47062F4D077D5A658AB41C3D9C3ADD6DEF200E7F242D5ED84",
        "0A7389EC6A7AB71F6CE813FB898A530AF1A3C800F849BF56AAE0C7A12AF1C0EE",
        "210863A29533A0C848DE893CD1BC0256D8B3DDD3439EE55BC94EB77F71AC2D99",
        "4B4FD1F08738F53183AC85B3C6E4EE1F8E97E0154DF668EC700131D4167B93D6",
        "180ED760DED7C1899F6F53116EA6C9B54AB809809AE05E821C2E4B0B3CCCBF6D",
        "643F5AFF2DD6EA235F2E53EFCCD6009F560E1C0EB01163E1415B2176A2679F8A",
        "3845884F3FFAC354449BE949B849325EC0D66AF841825DBF6BD668BB91A49C15",
        "0BE9B911A60E285C2FFA50F0380BCB86ED85BF7114C2C0D0AA8E7E6FB3335146",
        "4A9DE74B4219EBF351933831D1F5B53467F856ADFA7B478C428027DD408F61FF",
        "4EB9D94D0EE8C3E79E0265B0635AF17DB6AA7CA1B463B70E4C51FFFB7F8403C9",
        "4C9315A7B48D8A11FFD23510E0936842AE8368DEDFB511A01DFC930C96D8EE26"
    };

    private readonly string[][] _basicTestVerifierQueries =
    {
        new[]
        {
            "1CAD5F196369693D0CFBA21A7DAD0EBC5C426CEBC959F9ADEC1B104B92AAADFE",
            "0000000000000000000000000000000000000000000000000000000000000000",
            "1C0727F0C6C9887189F75A9D08B804ABA20892A238E147750767EAC22A830D08"
        },
        new[]
        {
            "1CAD5F196369693D0CFBA21A7DAD0EBC5C426CEBC959F9ADEC1B104B92AAADFE",
            "0100000000000000000000000000000000000000000000000000000000000000",
            "5F2C14B814DA6F3A65634C9C38489A3BD31B9799ADEA4FBA1D355B65EB363808"
        },
        new[]
        {
            "1CAD5F196369693D0CFBA21A7DAD0EBC5C426CEBC959F9ADEC1B104B92AAADFE",
            "0200000000000000000000000000000000000000000000000000000000000000",
            "D4C2E0C6C8C70D8475AAC44DAA2A7FBA201CC0F24E35CE19D4DB811BA1D24F03"
        },
        new[]
        {
            "1CAD5F196369693D0CFBA21A7DAD0EBC5C426CEBC959F9ADEC1B104B92AAADFE",
            "0300000000000000000000000000000000000000000000000000000000000000",
            "5A7F0C320563D27E16C0FD11F499A264E383010A922D9F2911CA3399EEAD2617"
        },
        new[]
        {
            "56778FE0BCF12A14820D4C054D85CFCAE4BDB7017107B6769CECD42629A3825E",
            "0000000000000000000000000000000000000000000000000000000000000000",
            "0100000000000000000000000000000000000000000000000000000000000000"
        },
        new[]
        {
            "56778FE0BCF12A14820D4C054D85CFCAE4BDB7017107B6769CECD42629A3825E",
            "0100000000000000000000000000000000000000000000000000000000000000",
            "0000000000000000000000000000000000000000000000000000000000000000"
        },
        new[]
        {
            "56778FE0BCF12A14820D4C054D85CFCAE4BDB7017107B6769CECD42629A3825E",
            "0200000000000000000000000000000000000000000000000000000000000000",
            "B79BB0AA775611F15CC999E5AE7E2ED20D514C45E8FB028AFACBE454AA1B110B"
        },
        new[]
        {
            "38F30E21C79747190371DF99E88B886638BE445D44F8F9B56CA7C062EA329944",
            "0000000000000000000000000000000000000000000000000000000000000000",
            "0000000000000000000000000000000001000000000000000000000000000000"
        },
        new[]
        {
            "38F30E21C79747190371DF99E88B886638BE445D44F8F9B56CA7C062EA329944",
            "0100000000000000000000000000000000000000000000000000000000000000",
            "0000000000000000000000000000000000000000000000000000000000000000"
        },
        new[]
        {
            "6C650CE85C8B5D3CB5CCEF8BE82858AA2FA9C2CAD512086DB521BD2823E3FC38",
            "0000000000000000000000000000000000000000000000000000000000000000",
            "0100000000000000000000000000000000000000000000000000000000000000"
        },
        new[]
        {
            "6C650CE85C8B5D3CB5CCEF8BE82858AA2FA9C2CAD512086DB521BD2823E3FC38",
            "0100000000000000000000000000000000000000000000000000000000000000",
            "0100000000000000000000000000000000000000000000000000000000000000"
        },
        new[]
        {
            "6C650CE85C8B5D3CB5CCEF8BE82858AA2FA9C2CAD512086DB521BD2823E3FC38",
            "0200000000000000000000000000000000000000000000000000000000000000",
            "76F1E00A62F7EE90519BC4D74F17C6791067FA36059A2A05463CC30152BE840C"
        },
        new[]
        {
            "107802129C490EDADBAB32EC891EE6310E4F4F00E0056CE3BB0FFC6840A27577",
            "0000000000000000000000000000000000000000000000000000000000000000",
            "0100000000000000000000000000000001000000000000000000000000000000"
        },
        new[]
        {
            "107802129C490EDADBAB32EC891EE6310E4F4F00E0056CE3BB0FFC6840A27577",
            "0100000000000000000000000000000000000000000000000000000000000000",
            "0000000000000000000000000000000000000000000000000000000000000000"
        },
        new[]
        {
            "556A6FA8933AB25DDB0FB102FCA932AC58A5F539F83B9BFB6E21EA742AA5AD74",
            "0000000000000000000000000000000000000000000000000000000000000000",
            "0100000000000000000000000000000000000000000000000000000000000000"
        },
        new[]
        {
            "556A6FA8933AB25DDB0FB102FCA932AC58A5F539F83B9BFB6E21EA742AA5AD74",
            "0100000000000000000000000000000000000000000000000000000000000000",
            "0200000000000000000000000000000000000000000000000000000000000000"
        },
        new[]
        {
            "556A6FA8933AB25DDB0FB102FCA932AC58A5F539F83B9BFB6E21EA742AA5AD74",
            "0200000000000000000000000000000000000000000000000000000000000000",
            "79CB55FCCD395157E2840F6BE83010E565CA342C355C029E014C6EB62071A002"
        },
        new[]
        {
            "03F36F9C0821D7014E7A7B917C1D3B72ACF724906A30A8FEFB09889C3E4CFBC5",
            "0000000000000000000000000000000000000000000000000000000000000000",
            "0200000000000000000000000000000001000000000000000000000000000000"
        },
        new[]
        {
            "03F36F9C0821D7014E7A7B917C1D3B72ACF724906A30A8FEFB09889C3E4CFBC5",
            "0100000000000000000000000000000000000000000000000000000000000000",
            "0000000000000000000000000000000000000000000000000000000000000000"
        },
        new[]
        {
            "28A40FD3331B653DEA7BE3FE55BC1A0451E0DBAD672EC0236CAC46E5B23D13D5",
            "0000000000000000000000000000000000000000000000000000000000000000",
            "0100000000000000000000000000000000000000000000000000000000000000"
        },
        new[]
        {
            "28A40FD3331B653DEA7BE3FE55BC1A0451E0DBAD672EC0236CAC46E5B23D13D5",
            "0100000000000000000000000000000000000000000000000000000000000000",
            "0300000000000000000000000000000000000000000000000000000000000000"
        },
        new[]
        {
            "28A40FD3331B653DEA7BE3FE55BC1A0451E0DBAD672EC0236CAC46E5B23D13D5",
            "0200000000000000000000000000000000000000000000000000000000000000",
            "0C2B7E3D1BB320374B65ADAFA3FB9F5676997FF5E8D6A5C02ACF34137D9D120C"
        },
        new[]
        {
            "562743B585BEAF3DC1987C9BDF5701AF9C4784A3925499BD6318B63CCEC4B35F",
            "0000000000000000000000000000000000000000000000000000000000000000",
            "0300000000000000000000000000000001000000000000000000000000000000"
        },
        new[]
        {
            "562743B585BEAF3DC1987C9BDF5701AF9C4784A3925499BD6318B63CCEC4B35F",
            "0100000000000000000000000000000000000000000000000000000000000000",
            "0000000000000000000000000000000000000000000000000000000000000000"
        }
    };

    private readonly FrE[] _poly =
    {
        FrE.SetElement(1), FrE.SetElement(2), FrE.SetElement(3), FrE.SetElement(4), FrE.SetElement(5),
        FrE.SetElement(6), FrE.SetElement(7), FrE.SetElement(8), FrE.SetElement(9), FrE.SetElement(10),
        FrE.SetElement(11), FrE.SetElement(12), FrE.SetElement(13), FrE.SetElement(14), FrE.SetElement(15),
        FrE.SetElement(16), FrE.SetElement(17), FrE.SetElement(18), FrE.SetElement(19), FrE.SetElement(20),
        FrE.SetElement(21), FrE.SetElement(22), FrE.SetElement(23), FrE.SetElement(24), FrE.SetElement(25),
        FrE.SetElement(26), FrE.SetElement(27), FrE.SetElement(28), FrE.SetElement(29), FrE.SetElement(30),
        FrE.SetElement(31), FrE.SetElement(32)
    };

    [Test]
    public void TestBasicMSM()
    {
        List<FrE> polyEvalA = new();

        for (int i = 0; i < 256; i++) polyEvalA.Add(FrE.SetElement(i));

        CRS crs = CRS.Instance;
        for (int i = 0; i < 1000; i++)
        {
            Banderwagon cA = crs.Commit(polyEvalA.ToArray());
            Console.WriteLine(cA.ToBytes());
        }
    }

    [Test]
    public void TestBasicMultiProof()
    {
        List<FrE> polyEvalA = new();
        List<FrE> polyEvalB = new();

        for (int i = 0; i < 8; i++)
        {
            polyEvalA.AddRange(_poly);
            polyEvalB.AddRange(_poly.Reverse());
        }

        CRS crs = CRS.Instance;
        Banderwagon cA = crs.Commit(polyEvalA.ToArray());
        Banderwagon cB = crs.Commit(polyEvalB.ToArray());

        byte[] zs = { 0, 0 };
        FrE[] ys = { FrE.SetElement(1), FrE.SetElement(32) };
        FrE[][] fs = { polyEvalA.ToArray(), polyEvalB.ToArray() };

        Banderwagon[] cs = { cA, cB };


        VerkleProverQuery queryA = new(new LagrangeBasis(fs[0]), cs[0], zs[0], ys[0]);
        VerkleProverQuery queryB = new(new LagrangeBasis(fs[1]), cs[1], zs[1], ys[1]);

        MultiProof multiproof = new(crs, PreComputedWeights.Instance);

        Transcript proverTranscript = new("test");
        VerkleProverQuery[] queries = { queryA, queryB };
        VerkleProofStruct proof = multiproof.MakeMultiProof(proverTranscript, new List<VerkleProverQuery>(queries));
        FrE pChallenge = proverTranscript.ChallengeScalar("state");

        Assert.That(Convert.ToHexString(pChallenge.ToBytes()).ToLower()
            .SequenceEqual("eee8a80357ff74b766eba39db90797d022e8d6dee426ded71234241be504d519"), Is.True);

        Transcript verifierTranscript = new("test");
        VerkleVerifierQuery queryAx = new(cs[0], zs[0], ys[0]);
        VerkleVerifierQuery queryBx = new(cs[1], zs[1], ys[1]);

        VerkleVerifierQuery[] queriesX = { queryAx, queryBx };
        bool ok = multiproof.CheckMultiProof(verifierTranscript, queriesX, proof);
        Assert.That(ok, Is.True);

        FrE vChallenge = verifierTranscript.ChallengeScalar("state");
        Assert.That(vChallenge, Is.EqualTo(pChallenge));
    }

    [Test]
    public void TestBasicFailingProofTest()
    {
        Transcript proverTranscript = new("vt");
        MultiProof multiproof = new(CRS.Instance, PreComputedWeights.Instance);
        List<VerkleVerifierQuery> queries = (
            from queryString in _basicTestVerifierQueries
            let point = new Banderwagon(queryString[0])
            let childIndex = Convert.FromHexString(queryString[1])[0]
            let childHash = FrE.FromBytesReduced(Convert.FromHexString(queryString[2]))
            select new VerkleVerifierQuery(point, childIndex, childHash)
        ).ToList();

        Banderwagon d = new(_basicProofStruct[0]);
        FrE a = FrE.FromBytesReduced(Convert.FromHexString(_basicProofStruct[1]));
        Banderwagon[] l = new Banderwagon[8];
        Banderwagon[] r = new Banderwagon[8];

        for (int i = 2; i < 10; i++) l[i - 2] = new Banderwagon(_basicProofStruct[i]);

        for (int i = 10; i < 18; i++) r[i - 10] = new Banderwagon(_basicProofStruct[i]);

        IpaProofStruct ipaProof = new(l, a, r);
        VerkleProofStruct proof = new(ipaProof, d);

        Assert.That(multiproof.CheckMultiProof(proverTranscript, queries.ToArray(), proof), Is.True);
    }

    [Test]
    public void TestRandomProofGenerationAndVerification()
    {
        MultiProof prover = new(CRS.Instance, PreComputedWeights.Instance);
        VerkleProverQuery[] proverQueries = GenerateRandomQueries(400).ToArray();
        Transcript proverTranscript = new("test");
        VerkleProofStruct proof = prover.MakeMultiProof(proverTranscript, new List<VerkleProverQuery>(proverQueries));

        VerkleVerifierQuery[] verifierQueries = proverQueries
            .Select(x => new VerkleVerifierQuery(x.NodeCommitPoint, x.ChildIndex, x.ChildHash)).ToArray();
        Transcript verifierTranscript = new("test");
        bool verification = prover.CheckMultiProof(verifierTranscript, verifierQueries, proof);
        Assert.That(verification);
    }

    [Test]
    public void TestRustRandomProofGenerationAndVerification()
    {
        MultiProof prover = new(CRS.Instance, PreComputedWeights.Instance);
        VerkleProverQuery[] proverQueries = GenerateRandomQueries(400).ToArray();
        Transcript proverTranscript = new("verkle");

        List<byte> input = new();
        foreach (VerkleProverQuery query in proverQueries)
        {
            input.AddRange(query.NodeCommitPoint.ToBytes());
            foreach (FrE eval in query.ChildHashPoly.Evaluations)
            {
                input.AddRange(eval.ToBytes());
            }
            input.Add(query.ChildIndex);
            input.AddRange(query.ChildHash.ToBytes());
        }

        IntPtr ctx = RustVerkleLib.VerkleContextNew();
        byte[] output = new byte[576];
        RustVerkleLib.VerkleProve(ctx, input.ToArray(), (UIntPtr)input.Count, output);

        VerkleProofStruct proof = prover.MakeMultiProof(proverTranscript, new List<VerkleProverQuery>(proverQueries));
        output.Should().BeEquivalentTo(proof.Encode());
    }

    [Test]
    public void TestProofVerificationFromSerialization()
    {
        List<VerkleProverQuery> proverQueries = GenerateRandomQueries(400);

        VerkleProverQuerySerialized[] proverQueriesSerialized = proverQueries
            .Select(VerkleProverQuerySerialized.CreateProverQuerySerialized)
            .ToArray();

        MultiProof multiproof = new(CRS.Instance, PreComputedWeights.Instance);

        VerkleProofStructSerialized proofStructSerialized = multiproof.MakeMultiProofSerialized(proverQueriesSerialized);

        VerkleVerifierQuerySerialized[] verifierQueries = proverQueries
            .Select(
                x => new VerkleVerifierQuerySerialized(
                    x.NodeCommitPoint.ToBytesUncompressedLittleEndian(),
                    x.ChildIndex,
                    x.ChildHash.ToBytes()
                )
            ).ToArray();

        bool result = multiproof.CheckMultiProofSerialized(verifierQueries, proofStructSerialized);
        Assert.That(result, Is.True);
    }

    [Test]
    public void TestProofVerificationCtoRust()
    {
        MultiProof prover = new(CRS.Instance, PreComputedWeights.Instance);
        VerkleProverQuery[] proverQueries = GenerateRandomQueries(400).ToArray();
        Transcript proverTranscript = new("verkle");

        List<byte> input = new();
        foreach (VerkleProverQuery query in proverQueries)
        {
            input.AddRange(query.NodeCommitPoint.ToBytes());
            foreach (FrE eval in query.ChildHashPoly.Evaluations)
            {
                input.AddRange(eval.ToBytes());
            }
            input.Add(query.ChildIndex);
            input.AddRange(query.ChildHash.ToBytes());
        }

        VerkleProofStruct proof = prover.MakeMultiProof(proverTranscript, new List<VerkleProverQuery>(proverQueries));

        VerkleVerifierQuery[] verifierQueries = proverQueries
            .Select(x => new VerkleVerifierQuery(x.NodeCommitPoint, x.ChildIndex, x.ChildHash)).ToArray();

        input.Clear();
        input.AddRange(proof.Encode());
        foreach (VerkleVerifierQuery query in verifierQueries)
        {
            input.AddRange(query.NodeCommitPoint.ToBytes());
            input.Add(query.ChildIndex);
            input.AddRange(query.ChildHash.ToBytes());
        }

        IntPtr ctx = RustVerkleLib.VerkleContextNew();

        bool result = RustVerkleLib.VerkleVerify(ctx, input.ToArray(), (UIntPtr)input.Count);
        Assert.That(result, Is.True);
    }

    [Test]
    public void TestProofVerificationRust()
    {
        MultiProof prover = new(CRS.Instance, PreComputedWeights.Instance);
        VerkleProverQuery[] proverQueries = GenerateRandomQueries(400).ToArray();
        Transcript proverTranscript = new("verkle");

        List<byte> input = new();
        foreach (VerkleProverQuery query in proverQueries)
        {
            input.AddRange(query.NodeCommitPoint.ToBytes());
            foreach (FrE eval in query.ChildHashPoly.Evaluations)
            {
                input.AddRange(eval.ToBytes());
            }
            input.Add(query.ChildIndex);
            input.AddRange(query.ChildHash.ToBytes());
        }

        IntPtr ctx = RustVerkleLib.VerkleContextNew();

        byte[] output = new byte[576];
        RustVerkleLib.VerkleProve(ctx, input.ToArray(), (UIntPtr)input.Count, output);

        VerkleVerifierQuery[] verifierQueries = proverQueries
            .Select(x => new VerkleVerifierQuery(x.NodeCommitPoint, x.ChildIndex, x.ChildHash)).ToArray();

        input.Clear();
        input.AddRange(output);
        foreach (VerkleVerifierQuery query in verifierQueries)
        {
            input.AddRange(query.NodeCommitPoint.ToBytes());
            input.Add(query.ChildIndex);
            input.AddRange(query.ChildHash.ToBytes());
        }

        bool result = RustVerkleLib.VerkleVerify(ctx, input.ToArray(), (UIntPtr)input.Count);
        Assert.That(result, Is.True);
    }

    public static List<VerkleProverQuery> GenerateRandomQueries(int numOfQueries)
    {
        CRS crs = CRS.Instance;
        List<VerkleProverQuery> proverQueries = new();
        Random rand = new(0);
        using IEnumerator<FrE> randFre = FrE.GetRandom().GetEnumerator();
        for (int i = 0; i < numOfQueries; i++)
        {
            randFre.MoveNext();
            FrE[] poly = new FrE[256];
            for (int j = 0; j < 256; j++)
            {
                poly[j] = randFre.Current;
                randFre.MoveNext();
            }

            Banderwagon commit = crs.Commit(poly);
            byte childIndex = (byte)rand.Next(255);

            proverQueries.Add(new VerkleProverQuery(new LagrangeBasis(poly), commit, childIndex, poly[childIndex]));
        }

        return proverQueries;
    }

    public static VerkleProverQuerySerialized[] GenerateRandomQueriesSerialized(int numOfQueries)
    {
        CRS crs = CRS.Instance;
        VerkleProverQuerySerialized[] proverQueries = new VerkleProverQuerySerialized[numOfQueries];

        Random rand = new(0);
        using IEnumerator<FrE> randFre = FrE.GetRandom().GetEnumerator();

        for (int i = 0; i < numOfQueries; i++)
        {
            randFre.MoveNext();
            FrE[] poly = new FrE[256];
            byte[][] bytesPoly = new byte[256][];
            for (int j = 0; j < 256; j++)
            {
                poly[j] = randFre.Current;
                bytesPoly[j] = randFre.Current.ToBytes();
                randFre.MoveNext();
            }

            Banderwagon commit = crs.Commit(poly);
            byte childIndex = (byte)rand.Next(255);

            proverQueries[i] = new VerkleProverQuerySerialized(
                bytesPoly,
                commit.ToBytesUncompressedLittleEndian(),
                childIndex,
                bytesPoly[childIndex]
            );
        }

        return proverQueries;
    }
}
