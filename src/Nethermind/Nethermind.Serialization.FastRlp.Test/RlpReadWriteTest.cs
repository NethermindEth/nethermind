// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Serialization.FastRlp.Instances;

namespace Nethermind.Serialization.FastRlp.Test;

public class RlpReadWriteTest
{
    [Test]
    public void HeterogeneousList()
    {
        var rlp = Rlp.Write(static (ref RlpWriter w) =>
        {
            w.WriteSequence(static (ref RlpWriter w) =>
            {
                w.WriteSequence(static (ref RlpWriter w) => { w.Write(42); });
                w.WriteSequence(static (ref RlpWriter w) =>
                {
                    w.Write("dog");
                    w.Write("cat");
                });
            });
        });

        var decoded = Rlp.Read(rlp, (scoped ref RlpReader r) =>
        {
            return r.ReadSequence(static (scoped ref RlpReader r) =>
            {
                var _1 = r.ReadSequence(static (scoped ref RlpReader r) => r.ReadInt32());
                var _2 = r.ReadSequence(static (scoped ref RlpReader r) =>
                {
                    var _1 = r.ReadString();
                    var _2 = r.ReadString();

                    return (_1, _2);
                });

                return (_1, _2);
            });
        });

        decoded.Should().Be((42, ("dog", "cat")));
    }

    [Test]
    public void LongList()
    {
        var rlp = Rlp.Write(static (ref RlpWriter w) =>
        {
            w.WriteSequence(static (ref RlpWriter w) =>
            {
                for (int i = 0; i < 100; i++)
                {
                    w.Write("dog");
                }
            });
        });

        List<string> decoded = Rlp.Read(rlp, (scoped ref RlpReader r) =>
        {
            return r.ReadSequence((scoped ref RlpReader r) =>
            {
                List<string> result = [];
                for (int i = 0; i < 100; i++)
                {
                    result.Add(r.ReadString());
                }

                return result;
            });
        });

        decoded.Count.Should().Be(100);
        decoded.Should().AllBeEquivalentTo("dog");
    }

    [Test]
    public void MutlipleLongList()
    {
        var rlp = Rlp.Write(static (ref RlpWriter w) =>
        {
            w.WriteSequence(static (ref RlpWriter w) =>
            {
                for (int i = 0; i < 100; i++)
                {
                    w.Write("dog");
                }
            });
            w.WriteSequence(static (ref RlpWriter w) =>
            {
                for (int i = 0; i < 50; i++)
                {
                    w.Write("cat");
                }
            });
        });

        var (dogs, cats) = Rlp.Read(rlp, (scoped ref RlpReader r) =>
        {
            var dogs = r.ReadSequence((scoped ref RlpReader r) =>
            {
                List<string> result = [];
                while (r.HasNext)
                {
                    result.Add(r.ReadString());
                }

                return result;
            });
            var cats = r.ReadSequence((scoped ref RlpReader r) =>
            {
                List<string> result = [];
                while (r.HasNext)
                {
                    result.Add(r.ReadString());
                }

                return result;
            });

            return (dogs, cats);
        });

        dogs.Count.Should().Be(100);
        dogs.Should().AllBeEquivalentTo("dog");

        cats.Count.Should().Be(50);
        cats.Should().AllBeEquivalentTo("cat");
    }

    [TestCase(2)]
    public void UnknownLengthList([Values(1, 3, 5, 10, 20)] int length)
    {
        var rlp = Rlp.Write((ref RlpWriter root) =>
        {
            root.WriteSequence((ref RlpWriter w) =>
            {
                for (int i = 0; i < length; i++)
                {
                    w.Write(42);
                }
            });
        });

        List<int> decoded = Rlp.Read(rlp, (scoped ref RlpReader r) =>
        {
            return r.ReadSequence((scoped ref RlpReader r) =>
            {
                List<int> result = [];
                while (r.HasNext)
                {
                    result.Add(r.ReadInt32());
                }

                return result;
            });
        });

        decoded.Count.Should().Be(length);
    }

    [Test]
    public void InvalidObjectReading()
    {
        var rlp = Rlp.Write(static (ref RlpWriter w) => { w.Write(42); });
        Action tryRead = () => Rlp.Read(rlp, (scoped ref RlpReader r) => { r.ReadSequence((scoped ref RlpReader _) => { }); });

        tryRead.Should().Throw<RlpReaderException>();
    }

    [Test]
    public void InvalidListReading()
    {
        var rlp = Rlp.Write(static (ref RlpWriter w) => { w.WriteSequence(static (ref RlpWriter _) => { }); });
        Func<int> tryRead = () => Rlp.Read(rlp, (scoped ref RlpReader r) => r.ReadInt32());

        tryRead.Should().Throw<RlpReaderException>();
    }

    [Test]
    public void Choice()
    {
        RefRlpReaderFunc<int> intReader = (scoped ref RlpReader r) => r.ReadInt32();
        RefRlpReaderFunc<int> wrappedReader = (scoped ref RlpReader r) => r.ReadSequence(intReader);

        var intRlp = Rlp.Write(static (ref RlpWriter w) => { w.Write(42); });
        var wrappedIntRlp = Rlp.Write(static (ref RlpWriter w) => w.WriteSequence(static (ref RlpWriter w) => { w.Write(42); }));

        foreach (var rlp in (byte[][]) [intRlp, wrappedIntRlp])
        {
            int decoded = Rlp.Read(rlp, (scoped ref RlpReader r) => r.Choice(wrappedReader, intReader));

            decoded.Should().Be(42);
        }
    }

    [Test]
    public void ChoiceDeep()
    {
        RefRlpReaderFunc<(string, string, string)> readerA = static (scoped ref RlpReader r) =>
        {
            return r.ReadSequence(static (scoped ref RlpReader r) =>
            {
                var _1 = r.ReadString();
                var _2 = r.ReadString();
                var _3 = r.ReadString();

                return (_1, _2, _3);
            });
        };
        RefRlpReaderFunc<(string, string, string)> readerB = static (scoped ref RlpReader r) =>
        {
            return r.ReadSequence(static (scoped ref RlpReader r) =>
            {
                var _1 = r.ReadString();
                var _2 = r.ReadString();
                var _3 = r.ReadInt32();

                return (_1, _2, _3.ToString());
            });
        };

        var rlp = Rlp.Write(static (ref RlpWriter w) =>
        {
            w.WriteSequence(static (ref RlpWriter w) =>
            {
                w.Write("dog");
                w.Write("cat");
                w.Write(42);
            });
        });

        var decoded = Rlp.Read(rlp, (scoped ref RlpReader r) => r.Choice(readerA, readerB));
        decoded.Should().Be(("dog", "cat", "42"));
    }

    [Test]
    public void UserDefinedRecord()
    {
        List<Student> students =
        [
            new("Ana", 23, new Dictionary<string, int>
            {
                { "Math", 7 },
                { "Literature", 9 }
            }),
            new("Bob", 25, new Dictionary<string, int>
            {
                { "Math", 9 },
                { "Literature", 6 }
            }),
        ];

        var rlp = Rlp.Write((ref RlpWriter w) =>
        {
            w.WriteSequence((ref RlpWriter w) =>
            {
                foreach (var student in students)
                {
                    w.Write(student);
                }
            });
        });

        var decoded = Rlp.Read(rlp, static (scoped ref RlpReader r) =>
        {
            return r.ReadSequence(static (scoped ref RlpReader r) =>
            {
                List<Student> result = [];
                while (r.HasNext)
                {
                    result.Add(r.ReadStudent());
                }

                return result;
            });
        });

        decoded.Should().BeEquivalentTo(students);
    }

    [Test]
    public void ListCollection()
    {
        var list = new List<string> { "cat", "dog" };

        var rlp = Rlp.Write((ref RlpWriter w) => w.Write<string, StringRlpConverter>(list));

        var rlpExplicit = Rlp.Write(static (ref RlpWriter w) =>
        {
            w.WriteSequence(static (ref RlpWriter w) =>
            {
                w.Write("cat");
                w.Write("dog");
            });
        });
        rlpExplicit.Should().BeEquivalentTo(rlp);

        var decoded = Rlp.Read(rlp, static (scoped ref RlpReader r) => r.ReadList<string, StringRlpConverter>());

        list.Should().BeEquivalentTo(decoded);
    }

    [Test]
    public void DictionaryCollection()
    {
        var dictionary = new Dictionary<int, string>
        {
            { 1, "dog" },
            { 2, "cat" },
        };

        var rlp = Rlp.Write((ref RlpWriter w) => w.Write<int, string, Int32RlpConverter, StringRlpConverter>(dictionary));

        var rlpExplicit = Rlp.Write((ref RlpWriter w) =>
        {
            w.WriteSequence((ref RlpWriter w) =>
            {
                foreach (var (k, v) in dictionary)
                {
                    w.WriteSequence((ref RlpWriter w) =>
                    {
                        w.Write(k);
                        w.Write(v);
                    });
                }
            });
        });
        rlp.Should().BeEquivalentTo(rlpExplicit);

        var decoded = Rlp.Read(rlp, static (scoped ref RlpReader r) => r.ReadDictionary<int, string, Int32RlpConverter, StringRlpConverter>());

        decoded.Should().BeEquivalentTo(dictionary);
    }
}
