// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.FastRlp.Instances;

namespace Nethermind.Serialization.FastRlp.Test;

public record Student(string Name, int Age, Dictionary<string, int> Scores);

// NOTE: All the following code can be automatically derived using Source Generators
public abstract class StudentRlpConverter : IRlpConverter<Student>
{
    public static Student Read(ref RlpReader reader)
    {
        return reader.ReadList(static (scoped ref RlpReader r) =>
        {
            var name = r.ReadString();
            var age = r.ReadInt32();
            var scores = r.ReadList(static (scoped ref RlpReader r) =>
            {
                Dictionary<string, int> scores = [];
                while (r.HasNext)
                {
                    var subject = r.ReadString();
                    var score = r.ReadInt32();

                    scores[subject] = score;
                }

                return scores;
            });

            return new Student(name, age, scores);
        });
    }

    public static void Write(ref RlpWriter writer, Student value)
    {
        writer.WriteList((ref RlpWriter w) =>
        {
            w.Write(value.Name);
            w.Write(value.Age);
            w.WriteList((ref RlpWriter w) =>
            {
                foreach (var (subject, score) in value.Scores)
                {
                    w.Write(subject);
                    w.Write(score);
                }
            });
        });
    }
}

public static class StudentExt
{
    public static Student ReadStudent(this ref RlpReader reader) => StudentRlpConverter.Read(ref reader);
    public static void Write(this ref RlpWriter writer, Student value) => StudentRlpConverter.Write(ref writer, value);
}
