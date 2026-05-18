// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using NUnit.Framework;
using FluentAssertions;
using Nethermind.Serialization.FluentRlp.Instances;

namespace Nethermind.Serialization.FluentRlp.Test;

public class RlpReadWriteTest
{
    [Test]
    public void LongString()
    {
        var rlp = Rlp.Write(static (ref RlpWriter w) =>
        {
            var str = new string('A', 2000);
            w.Write(str);
        });

        var decoded = Rlp.Read(rlp, (scoped ref RlpReader r) => r.ReadString());

        decoded.Should().Be(new string('A', 2000));
    }

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

        List<string> decoded = Rlp.Read(rlp, static (scoped ref RlpReader r) =>
        {
            return r.ReadSequence(static (scoped ref RlpReader r) =>
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
    public void MultipleLongList()
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

        var (dogs, cats) = Rlp.Read(rlp, static (scoped ref RlpReader r) =>
        {
            var dogs = r.ReadSequence(static (scoped ref RlpReader r) =>
            {
                List<string> result = [];
                while (r.HasNext)
                {
                    result.Add(r.ReadString());
                }

                return result;
            });
            var cats = r.ReadSequence(static (scoped ref RlpReader r) =>
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
        var rlp = Rlp.Write(length, static (ref RlpWriter root, int length) =>
        {
            root.WriteSequence(length, static (ref RlpWriter w, int length) =>
            {
                for (int i = 0; i < length; i++)
                {
                    w.Write(42);
                }
            });
        });

        List<int> decoded = Rlp.Read(rlp, static (scoped ref RlpReader r) =>
        {
            return r.ReadSequence(static (scoped ref RlpReader r) =>
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
        Action tryRead = () => Rlp.Read(rlp, static (scoped ref RlpReader r) =>
        {
            return r.ReadSequence(static (scoped ref RlpReader _) => null as object);
        });

        tryRead.Should().Throw<RlpReaderException>();
    }

    [Test]
    public void InvalidListReading()
    {
        var rlp = Rlp.Write(static (ref RlpWriter w) => { w.WriteSequence(static (ref RlpWriter _) => { }); });
        Func<int> tryRead = () => Rlp.Read(rlp, static (scoped ref RlpReader r) => r.ReadInt32());

        tryRead.Should().Throw<RlpReaderException>();
    }

    [Test]
    public void Choice()
    {
        RefRlpReaderFunc<int> intReader = static (scoped ref RlpReader r) => r.ReadInt32();
        RefRlpReaderFunc<int> wrappedReader = (scoped ref RlpReader r) => r.ReadSequence(intReader);
        var intRlp = Rlp.Write(static (ref RlpWriter w) => { w.Write(42); });
        var wrappedIntRlp = Rlp.Write(static (ref RlpWriter w) => w.WriteSequence(static (ref RlpWriter w) => { w.Write(42); }));

        foreach (var rlp in (byte[][])[intRlp, wrappedIntRlp])
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
    public void OptionalStruct()
    {
        int? value = null;

        var rlp = Rlp.Write(value, static (ref RlpWriter w, int? value) =>
        {
            if (value.HasValue)
            {
                w.Write(value.Value);
            }
        });

        var decoded = Rlp.Read(rlp, static (scoped ref RlpReader r) =>
        {
            return r.Optional(static (scoped ref RlpReader r) => r.ReadInt32());
        });

        decoded.Should().Be(value);
    }


    [Test]
    public void OptionalReference()
    {
        string? value = null;

        var rlp = Rlp.Write(value, static (ref RlpWriter w, string? value) =>
        {
            if (value is not null)
            {
                w.Write(value);
            }
        });

        var decoded = Rlp.Read(rlp, static (scoped ref RlpReader r) =>
        {
            return r.Optional(static (scoped ref RlpReader r) => r.ReadString());
        });

        decoded.Should().Be(value);
    }

    [Test]
    public void OptionalDeep()
    {
        (string, string?, int, int?) tuple = ("dog", null, 42, null);

        var rlp = Rlp.Write(tuple, static (ref RlpWriter w, (string _1, string? _2, int _3, int? _4) tuple) =>
        {
            w.Write(tuple._1);
            if (tuple._2 is not null)
            {
                w.Write(tuple._2);
            }
            w.Write(tuple._3);
            if (tuple._4.HasValue)
            {
                w.Write(tuple._4.Value);
            }
        });

        var decoded = Rlp.Read(rlp, static (scoped ref RlpReader r) =>
        {
            var _1 = r.ReadString();
            var _2 = r.Optional(static (scoped ref RlpReader r) => r.ReadString());
            var _3 = r.ReadInt32();
            var _4 = r.Optional(static (scoped ref RlpReader r) => r.ReadInt32());

            return (_1, _2, _3, _4);
        });

        decoded.Should().Be(tuple);
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

        var rlp = Rlp.Write(students, static (ref RlpWriter w, List<Student> students) =>
        {
            w.WriteSequence(students, static (ref RlpWriter w, List<Student> students) =>
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

        var rlp = Rlp.Write(list, static (ref RlpWriter w, List<string> list) => w.Write(list, StringRlpConverter.Write));

        var rlpExplicit = Rlp.Write(static (ref RlpWriter w) =>
        {
            w.WriteSequence(static (ref RlpWriter w) =>
            {
                w.Write("cat");
                w.Write("dog");
            });
        });
        rlpExplicit.Should().BeEquivalentTo(rlp);

        var decoded = Rlp.Read(rlp, static (scoped ref RlpReader r) => r.ReadList(StringRlpConverter.Read));

        list.Should().BeEquivalentTo(decoded);
    }

    [Test]
    public void ListOfListCollection()
    {
        List<List<string>> list = [
            ["dog", "cat"],
            ["foo"],
            []
        ];

        var rlp = Rlp.Write(list, static (ref RlpWriter w, List<List<string>> list) =>
            w.Write(list, static (ref RlpWriter w, List<string> v) =>
                w.Write(v, StringRlpConverter.Write)));

        var rlpExplicit = Rlp.Write(static (ref RlpWriter w) =>
        {
            w.WriteSequence(static (ref RlpWriter w) =>
            {
                w.WriteSequence(static (ref RlpWriter w) =>
                {
                    w.Write("dog");
                    w.Write("cat");
                });

                w.WriteSequence(static (ref RlpWriter w) =>
                {
                    w.Write("foo");
                });

                w.WriteSequence(static (ref RlpWriter _) =>
                {
                });
            });
        });
        rlpExplicit.Should().BeEquivalentTo(rlp);

        var decoded = Rlp.Read(rlp, static (scoped ref RlpReader r) =>
            r.ReadList(static (scoped ref RlpReader r) =>
                r.ReadList(StringRlpConverter.Read)));

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

        var rlp = Rlp.Write(dictionary, static (ref RlpWriter w, Dictionary<int, string> dictionary) =>
            w.Write(dictionary, Int32RlpConverter.Write, StringRlpConverter.Write));

        var rlpExplicit = Rlp.Write(dictionary, static (ref RlpWriter w, Dictionary<int, string> dictionary) =>
        {
            w.WriteSequence(dictionary, static (ref RlpWriter w, Dictionary<int, string> dictionary) =>
            {
                foreach (var tuple in dictionary)
                {
                    w.WriteSequence(tuple, static (ref RlpWriter w, KeyValuePair<int, string> tuple) =>
                    {
                        w.Write(tuple.Key);
                        w.Write(tuple.Value);
                    });
                }
            });
        });
        rlp.Should().BeEquivalentTo(rlpExplicit);

        var decoded = Rlp.Read(rlp, static (scoped ref RlpReader r) =>
            r.ReadDictionary(Int32RlpConverter.Read, StringRlpConverter.Read));

        decoded.Should().BeEquivalentTo(dictionary);
    }

    [Test]
    public void TupleCollection()
    {
        var tuple = (42, 1337);

        var rlp = Rlp.Write(tuple, static (ref RlpWriter w, (int, int) tuple)
            => w.Write(tuple, Int32RlpConverter.Write, Int32RlpConverter.Write));

        var rlpExplicit = Rlp.Write(tuple, static (ref RlpWriter w, (int, int) tuple) =>
        {
            w.Write(tuple.Item1);
            w.Write(tuple.Item2);
        });

        rlp.Should().BeEquivalentTo(rlpExplicit);

        var decoded = Rlp.Read(rlp, static (scoped ref RlpReader r) =>
            r.ReadTuple(Int32RlpConverter.Read, Int32RlpConverter.Read));

        decoded.Should().BeEquivalentTo(tuple);
    }
}
