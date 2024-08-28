
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Nethermind.Evm.CodeAnalysis
{

    public record struct NGramInfo(uint Freq, uint Count, bool Enqueued = false, bool Updated = true)
    {

        public NGramInfo MarkFresh()
        {
            return this with { Updated = true };
        }

        public NGramInfo MarkStale()
        {
            return this with { Updated = false };
        }

        public NGramInfo UpdateFreq(uint freq)
        {
            return this with { Freq = freq };
        }

        public NGramInfo IncCount()
        {
            return this with { Count = Count + 1 };
        }

        public NGramInfo Enqueue()
        {
            return this with { Enqueued = true };
        }
        public NGramInfo Dequeue()
        {
            return this with { Enqueued = false };
        }
    }

    public class StatsAnalyzer
    {
        private static StatsAnalyzer? _instance;
        private static readonly object _lock = new object();

        private byte[] _buffer;
        int _start = 0;
        int _end = 0;


        private static readonly object _topNLock = new object();
        private PriorityQueue<ulong, uint> _topNQueue;
        private SortedSet<(ulong, uint)> _topN;
        private Dictionary<ulong, uint> _topNMap;
        private Dictionary<ulong, NGramInfo> _topNStatMap;

        private int _block = 0;

        public CMSketch _sketch;

        private List<Task> _tasks = new List<Task>();
        private Task _blockTask = Task.CompletedTask;
        private static readonly object _taskLock = new object();

        private string _dir;
        private string _fullPath;
        private int _n;

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

        public static int Count
        {
            get
            {
                lock (_lock)
                {
                    if (_instance != null)
                    {
                        return _instance._topNQueue.Count;
                    }
                }
                return 0;
            }
        }

        private StatsAnalyzer(int topN, int bufferSize, string statFileName, string dir = "/media/usb/stats/")
        {
            _dir = dir;
            _n = topN;
            _fullPath = _dir + statFileName + ".stats.txt";
            _sketch = new CMSketch(2, 600000);
            _topN = new SortedSet<(ulong ngram, uint count)>(new TopNComparer());
            _topNMap = new Dictionary<ulong, uint>();
            _buffer = new byte[bufferSize];
            _topNQueue = new PriorityQueue<ulong, uint>(topN);
            _topNStatMap = new Dictionary<ulong, NGramInfo>();
            SetStatFileName(statFileName);
        }

        public static StatsAnalyzer GetInstance(int topN, int bufferSize, string statFileName, string dir = "/media/usb/stats/")
        {
            lock (_lock)
            {
                if (_instance == null)
                {
                    _instance = new StatsAnalyzer(topN, bufferSize, statFileName, dir);
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


        public static void NoticeBlockCompletion()
        {
            lock (_lock)
            {
                if (_instance != null)
                {
                    ++_instance._block;
                    if (_instance._block % 2 == 0)
                    {
                        Task blockTask = Task.Run(() =>
                                {
                                    _instance.WaitForCompletion();
                                    _instance.RefreshQueue();
                                    _instance._sketch.Reset();
                                });
                        _instance._blockTask = blockTask;
                    }
                }
            }
        }

        public static void NoticeBlockCompletionBlocking()
        {
            lock (_lock)
            {
                if (_instance != null)
                {
                    ++_instance._block;
                    if (_instance._block % 2 == 0)
                    {
                        _instance.WaitForCompletion();
                        _instance.RefreshQueue();
                        _instance._sketch.Reset();
                    }
                }
            }
        }

        public static void NoticeTransactionCompletion()
        {
            lock (_lock)
            {
                if (_instance != null)
                {
                    _instance.SendTransactionForProcessing();
                }
            }
        }


        private bool HasCapacity()
        {
            return _end < _buffer.Length;
        }

        private void IncBlock()
        {
            _block = (_block + 1) & (int.MaxValue >> 1);
        }

        private void SendTransactionForProcessing()
        {
            if (_start < _end)
            {
                byte[] transaction = new byte[_end - _start];
                Buffer.BlockCopy(_buffer, _start, transaction, 0, _end - _start);
                _end = _start;
                Task task = Task.Run(() => ProcessTransaction(transaction, _block));
                lock (_taskLock)
                {
                    _tasks.Add(task);
                }
            }
        }


        private void ProcessTransaction(byte[] transaction, int block)
        {
            ulong ngram = 0;
            _blockTask.Wait();

            foreach (byte instruction in transaction)
            {
                ngram = (ngram << 8) | instruction;
                Console.WriteLine($"Adding instruction: {(Instruction)instruction}, Ngram SoFar : {AsString(ngram)}");
                for (int i = 1; i < 7; i++)
                {
                    if (byteIndexes[i - 1] < ngram) AddSequence2(ngram & ngramBitMaks[i], block);
                }
            }

        }

        private void WaitForCompletion()
        {
            Task[] tasksArray;

            lock (_taskLock)
            {
                tasksArray = _tasks.ToArray();
                _tasks.Clear();
            }

            Task.WaitAll(tasksArray);

        }


        public static void AddInstruction(Instruction instruction)
        {
            lock (_lock)
            {
                if (_instance != null)
                {
                    if (_instance.HasCapacity()) _instance.AddByte((byte)instruction);
                }
            }
        }

        public void AddByte(byte instruction)
        {
            _buffer[_end] = instruction;
            ++_end;
        }


        // utility for testing
        public static ulong AsNGram(Instruction[] instructions)
        {
            byte[] b = new byte[instructions.Length];

            for (int i = 0; i < instructions.Length; i++)
            {
                b[i] = (byte)instructions[i];
            }

            return AsNGram(b);
        }

        public static ulong AsNGram(byte[] instructions)
        {
            ulong ngram = 0;

            if (instructions.Length > 7)
            {
                throw new ArgumentException("The NGram instructions array cannot have more than 7 elements.");
            }

            foreach (byte instruction in instructions)
            {
                ngram = (ngram << 8) | instruction;
            }
            return ngram;
        }

        public static NGramInfo GetStatInfo(byte[] ngram)
        {
            lock (_lock)
            {
                if (_instance != null)
                {
                    lock (_topNLock)
                    {
                        if (_instance._topNStatMap.TryGetValue(AsNGram(ngram), out NGramInfo info)) return info;
                    }
                }
            }
            return new NGramInfo(0, 0);
        }

        public static NGramInfo GetStatInfo(Instruction[] ngram)
        {
            lock (_lock)
            {
                if (_instance != null)
                {
                    lock (_topNLock)
                    {
                        if (_instance._topNStatMap.TryGetValue(AsNGram(ngram), out NGramInfo info)) return info;
                    }
                }
            }
            return new NGramInfo(0, 0);
        }

        private void RefreshQueue()
        {
            lock (_topNLock)
            {
                Console.WriteLine("refreshing queue");
                int count = _topNQueue.Count;

                Span<(ulong opcodeSequence, uint freq)> stackAllocArray = stackalloc (ulong, uint)[count];
                for (int i = 0; i < count; i++)
                {
                    _topNQueue.TryDequeue(out ulong opcodeSequence, out uint freq);
                    //  if (!_topNStatMap[opcodeSequence].Updated)
                    //  {
                    _topNStatMap[opcodeSequence] = _topNStatMap[opcodeSequence].UpdateFreq(
                 ((uint)_block - 2) * _topNStatMap[opcodeSequence].Freq + _sketch.Query(opcodeSequence) >> ((int)(_block / 2))
                            );
                    // }

                    stackAllocArray[i] = (opcodeSequence, _topNStatMap[opcodeSequence].Freq);
                    _topNStatMap[opcodeSequence] = _topNStatMap[opcodeSequence].MarkStale();
                }

                for (int i = 0; i < count; i++)
                {
                    Console.WriteLine($"refreshed ngram: {AsString(stackAllocArray[i].Item1)} , freq: {stackAllocArray[i].Item2}");
                    _topNQueue.Enqueue(stackAllocArray[i].Item1, stackAllocArray[i].Item2);
                }
            }
        }

        private void AddSequence2(ulong opcodeSequence, int block)
        {


            lock (_topNLock)
            {
                Console.WriteLine($"Adding sequence {AsString(opcodeSequence)}");
                NGramInfo info;
                if (_topNStatMap.TryGetValue(opcodeSequence, out info))
                {
                    info.Freq = ((uint)block - 2) * info.Freq + _sketch.UpdateAndQuery(opcodeSequence) >> ((int)(block / 2));
                    ++info.Count;
                    _topNStatMap[opcodeSequence] = info.MarkFresh();
                    if (!info.Enqueued & _topNQueue.TryPeek(out ulong seq, out uint minFreq))
                    {

                        if (_topNQueue.Count < _n)
                        {
                            _topNQueue.Enqueue(opcodeSequence, info.Freq);
                        }
                        else if (minFreq < info.Freq)
                        {
                            _topNQueue.DequeueEnqueue(opcodeSequence, info.Freq);
                            _topNStatMap[seq] = (_topNStatMap[seq]).Dequeue();
                        }
                    }
                }
                else
                {
                    info = new NGramInfo(_sketch.UpdateAndQuery(opcodeSequence) >> ((int)(block / 2)), _sketch.Query(opcodeSequence));
                    Console.WriteLine($"Not in queue {AsString(opcodeSequence)} , info: {info}");

                    if (_topNQueue.TryPeek(out ulong seq, out uint minFreq))
                    {
                        if (_topNQueue.Count < _n)
                        {
                            _topNQueue.Enqueue(opcodeSequence, info.Freq);
                        }
                        else if (minFreq < info.Freq)
                        {
                            _topNQueue.DequeueEnqueue(opcodeSequence, info.Freq);
                            _topNStatMap[seq] = (_topNStatMap[seq]).Dequeue();
                        }

                        _topNStatMap.Add(opcodeSequence, info.Enqueue().MarkFresh());
                    }
                    else
                    {
                        // no values added yet so we add.
                        _topNQueue.Enqueue(opcodeSequence, info.Freq);
                        _topNStatMap.Add(opcodeSequence, info.Enqueue().MarkFresh());
                    }
                }
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


        public static void Reset()
        {
            _instance = null;
        }

        public static string AsString(Instruction[] ngram)
        {
            return AsString(AsNGram(ngram));

        }

        public static byte[] AsBytes(ulong ngram)
        {
            byte[] instructions = new byte[7];
            int i = 0;
            for (i = 0; i < instructions.Length; i++)
            {
                instructions[instructions.Length - 1 - i] = (byte)((ngram & byteIndexes[i]) >> (i * 8));
                if (instructions[instructions.Length - 1 - i] == (byte)Instruction.STOP)
                {
                    break;
                }
            }

            return instructions[(instructions.Length - i)..instructions.Length];
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
