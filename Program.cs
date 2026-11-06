using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace GuessingRace
{
    internal class Program
    {
        // Size of the secret number in bytes (1-8).
        // 4 bytes matches your screenshot and finishes in seconds to a few minutes.
        // 8 bytes (2^64 possibilities) would effectively never be guessed - see notes.
        private const int ByteCount = 4;
        private const int ThreadCount = 4;

        private static async Task Main()
        {
            ulong mask = ByteCount >= 8 ? ulong.MaxValue : (1UL << (8 * ByteCount)) - 1;
            ulong secret = RandomULong(Random.Shared) & mask;

            var workers = new Worker[ThreadCount];
            for (int i = 0; i < ThreadCount; i++)
                workers[i] = new Worker($"Thread{i + 1}");

            using var cts = new CancellationTokenSource();
            var winnerFound = new TaskCompletionSource<Worker>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            // Start the sub tasks. LongRunning gives each one its own dedicated thread.
            var tasks = new Task[ThreadCount];
            for (int i = 0; i < ThreadCount; i++)
            {
                Worker w = workers[i];
                tasks[i] = Task.Factory.StartNew(
                    () => w.Run(secret, mask, cts.Token, winnerFound),
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
            }

            var sw = Stopwatch.StartNew();
            Console.Clear();
            Console.CursorVisible = false;

            // Refresh once per second until someone wins (wakes early if a winner appears).
            while (!winnerFound.Task.IsCompleted)
            {
                Render(secret, workers, sw.Elapsed);
                await Task.WhenAny(winnerFound.Task, Task.Delay(1000));
            }

            // 1. Mark the winner.
            Worker winner = await winnerFound.Task;
            winner.IsWinner = true;
            Render(secret, workers, sw.Elapsed);

            // 2. Cancel everyone else and wait for them all to finish.
            cts.Cancel();
            await Task.WhenAll(tasks);

            // 3. Final display.
            Render(secret, workers, sw.Elapsed);
            Console.WriteLine();
            Console.WriteLine("All threads have now been stopped.");
            Console.CursorVisible = true;
        }

        private static void Render(ulong secret, Worker[] workers, TimeSpan elapsed)
        {
            int hexWidth = ByteCount * 5;
            int lineWidth = 12 + 2 + 10 + hexWidth + 16 + 10;

            void Line(string text) => Console.WriteLine(text.PadRight(lineWidth));

            Console.SetCursorPosition(0, 0);
            Line($"{"Correct Answer",-24}{ToHex(secret)}");
            Line($"Elapsed: {elapsed:hh\\:mm\\:ss}");
            Line("");
            Line($"{"# Guesses",12}  {"",-10}{"Last Guess".PadRight(hexWidth)}{"Status",-16}Results");

            foreach (var w in workers)
            {
                Line($"{w.Guesses,12:N0}  {w.Name,-10}{ToHex(w.LastGuess).PadRight(hexWidth)}" +
                     $"{w.Status,-16}{(w.IsWinner ? "Winner" : "")}");
            }
        }

        private static string ToHex(ulong value)
        {
            var parts = new string[ByteCount];
            for (int i = 0; i < ByteCount; i++)
            {
                int shift = 8 * (ByteCount - 1 - i);
                parts[i] = $"0x{(value >> shift) & 0xFF:X2}";
            }
            return string.Join(" ", parts);
        }

        internal static ulong RandomULong(Random rng) =>
            (ulong)rng.NextInt64(long.MinValue, long.MaxValue);
    }

    internal class Worker
    {
        private long _guesses;
        private long _lastGuess;
        private volatile string _status = "...running...";
        private volatile bool _isWinner;

        public Worker(string name) => Name = name;

        public string Name { get; }

        // Only the worker thread writes these; the main thread reads them.
        public long Guesses => Volatile.Read(ref _guesses);
        public ulong LastGuess => (ulong)Volatile.Read(ref _lastGuess);
        public string Status => _status;

        // Set by the main thread.
        public bool IsWinner
        {
            get => _isWinner;
            set => _isWinner = value;
        }

        public void Run(ulong secret, ulong mask, CancellationToken token,
                        TaskCompletionSource<Worker> winnerFound)
        {
            var rng = new Random();   // each thread gets its own Random
            long count = 0;

            while (!token.IsCancellationRequested)
            {
                ulong guess = Program.RandomULong(rng) & mask;
                count++;
                Volatile.Write(ref _guesses, count);
                Volatile.Write(ref _lastGuess, (long)guess);

                if (guess == secret)
                {
                    // TrySetResult guarantees only one winner, even if two match at once.
                    _status = winnerFound.TrySetResult(this) ? "Found it!" : "Stopped";
                    return;
                }
            }

            _status = "Stopped";
        }
    }
}