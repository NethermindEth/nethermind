
using System.Collections.Generic;
using System.IO;

namespace Nethermind.Evm.CodeAnalysis
{
    public class StatsAnalyzer
    {
        private static StatsAnalyzer? _instance;
        private static readonly object _lock = new object();

        private SortedSet<(ulong, uint)> _topN;
        private Dictionary<ulong, uint> _topNMap;
        public CMSketch _sketch;

        private string _dir;
        private string _fullPath;
        private uint _n;
        private ulong _ngram;

        const ulong twogramBitMask = (255UL << 8) | 255UL;
        const ulong threegramBitMask = (255UL << 8 * 2) | twogramBitMask;
        const ulong fourgramBitMask = (255U << 8 * 3) | threegramBitMask;
        const ulong fivegramBitMask = (255UL << 8 * 4) | fourgramBitMask;
        const ulong sixgramBitMask = (255UL << 8 * 5) | fivegramBitMask;
        const ulong sevengramBitMask = (255UL << 8 * 6) | sixgramBitMask;
        public ulong[] ngramBitMaks = [255UL, twogramBitMask, threegramBitMask, fourgramBitMask, fivegramBitMask, sixgramBitMask, sevengramBitMask];
        public static ulong[] byteIndexes = {
            255UL,
            255UL << 8,
            255UL << 16,
            255UL << 24,
            255UL << 32,
            255UL << 40,
            255UL << 48,
            255UL << 56
        };
        public static ulong[] byteIndexShifts = {
             0,
             8,
             16,
             24,
             32,
             40,
             48,
             56
        };

        private StatsAnalyzer(uint topN, string statFileName, string dir = "/media/usb/stats/")
        {
            _dir = dir;
            _n = topN;
            _fullPath = _dir + statFileName + ".stats.txt";
            _sketch = new CMSketch(2, 600000);
            _topN = new SortedSet<(ulong ngram, uint count)>(new TopNComparer());
            _topNMap = new Dictionary<ulong, uint>();
            SetStatFileName(statFileName);
        }

        public static StatsAnalyzer GetInstance(uint topN, string statFileName, string dir = "/media/usb/stats/")
        {
            lock (_lock)
            {
                if (_instance == null)
                {
                    _instance = new StatsAnalyzer(topN, statFileName, dir);
                }
                else
                {
                    _instance.SetStatFileName(statFileName);
                }
                return _instance;
            }
        }

        public void SetInstanceStatFileName(string statFileName)
        {
            lock (_lock)
            {
                if (_instance != null)
                {
                    _instance.SetStatFileName(statFileName);
                }
            }
        }

        public void SetStatFileName(string statFileName)
        {

            _fullPath = _dir + statFileName + ".stats.txt";
            // Ensure the log file is empty or create a new one
            if (File.Exists(_fullPath))
            {
                File.WriteAllText(_fullPath, string.Empty);  // Clear the file content
            }
        }

        public static void AddInstruction(Instruction instruction)
        {
            lock (_lock)
            {
                if (_instance != null)
                {
                    _instance.AddByte((byte)instruction);
                }
            }
        }

        public void AddByte(byte instruction)
        {
            _ngram = (_ngram << 8) | instruction;

            for (int i = 1; i < 7; i++)
            {
                if (byteIndexes[i] < _ngram) AddSequence(_ngram & ngramBitMaks[i]);
            }

        }

        private void AddSequence(ulong opcodeSequence)
        {
            uint count = _sketch.UpdateAndQuery(opcodeSequence);
            // If the sequence is already in _topN, remove it before updating
            if (_topNMap.ContainsKey(opcodeSequence))
            {
                _topN.Remove((opcodeSequence, _topNMap[opcodeSequence]));
            }

            //  if _topN already has n elements and the new count is greater than the smallest, remove the smallest
            if (_topN.Count >= _n && count > _topN.Min.Item2)
            {
                _topN.Remove(_topN.Min);

            }

            //  Add the new element if it either fits the _topN or improves the smallest value
            if (_topN.Count < _n || count > _topN.Min.Item2)
            {
                _topN.Add((opcodeSequence, count));
                _topNMap[opcodeSequence] = count;
            }
        }

        public void WriteTopN()
        {

            lock (_lock)
            {
                using (StreamWriter writer = new StreamWriter(_fullPath, true))
                {
                    if (_instance != null)
                    {


                        foreach ((ulong ngram, uint count) in _instance._topN)
                        {
                            writer.WriteLine($"{AsString(ngram)}Observed count {count}");
                        }

                    }
                    else
                    {


                        foreach ((ulong ngram, uint count) in _topN)
                        {
                            writer.WriteLine($"{AsString(ngram)}Observed count {count}");
                        }

                    }
                }
            }
        }


        public static string AsString(ulong ngram)
        {
            Instruction[] instructions = new Instruction[7];
            for (int i = 0; i < instructions.Length; i++)
            {
                instructions[i] = (Instruction)((byte)((ngram & byteIndexes[i]) >> (i * 8)));
            }
            return $"{instructions[6]} {instructions[5]} {instructions[4]} {instructions[3]} {instructions[2]} {instructions[1]} {instructions[0]}";

        }


    }

    public class TopNComparer : IComparer<(ulong ngram, uint count)>
    {
        public int Compare((ulong ngram, uint count) x, (ulong ngram, uint count) y)
        {
            var result = x.count.CompareTo(y.count);
            if (result == 0) result = x.ngram.CompareTo(y.ngram);
            return result;

        }

    }
}
