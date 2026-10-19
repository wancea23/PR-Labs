using System.Collections.Concurrent;
using System.Diagnostics;

const int Total = 50_000_000;

switch (args.Length > 0 ? args[0] : "")
{
    case "gen": Generate(args[1]); break;
    case "split": Split(args[1], args[2]); break;
    case "1a": { int t = int.Parse(args[2]); Run("1a", t, () => Task1a(args[1], t)); break; }
    case "1b": Task1b(args[1]); break;
    case "1c": { int t = int.Parse(args[2]); Run("1c", t, () => Task1c(args[1], t)); break; }
    case "2": { int t = int.Parse(args[2]); Run("2", t, () => Task2(args[1], t)); break; }
    default: Console.WriteLine("usage: Lab1 gen|split|1a|1b|1c|2 ..."); break;
}

static void Generate(string path)
{
    var random = new Random(42);
    using var w = File.CreateText(path);
    w.NewLine = "\n";
    for (int i = 0; i < Total; i++) w.WriteLine(random.Next(-1_000_000, 1_000_001));
}

static void Split(string path, string dir)
{
    Directory.CreateDirectory(dir);
    using var r = new StreamReader(path);
    for (int f = 1; f <= 8; f++)
    {
        using var w = File.CreateText(Path.Combine(dir, $"numbers_{f}.txt"));
        w.NewLine = "\n";
        for (int i = 0; i < Total / 8; i++) w.WriteLine(r.ReadLine());
    }
}

static long[] Task1a(string path, int threads)
{
    long size = new FileInfo(path).Length;
    var results = new long[threads][];
    RunThreads(threads, i =>
    {
        long from = LineStart(path, size * i / threads);
        long to = LineStart(path, size * (i + 1) / threads);
        results[i] = CountPart(path, from, to);
    });
    return Sum(results);
}

static long LineStart(string path, long pos)
{
    if (pos == 0) return 0;
    using var fs = File.OpenRead(path);
    if (pos >= fs.Length) return fs.Length;
    fs.Position = pos - 1;
    int b;
    while ((b = fs.ReadByte()) != '\n' && b != -1) { }
    return fs.Position;
}

static long[] CountPart(string path, long from, long to)
{
    var counter = new SignCounter();
    using var fs = File.OpenRead(path);
    fs.Position = from;
    var buffer = new byte[64 * 1024];
    long left = to - from;
    while (left > 0)
    {
        int n = fs.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
        if (n == 0) break;
        counter.Feed(buffer.AsSpan(0, n));
        left -= n;
    }
    counter.End();
    return counter.Counts;
}

static void Task1b(string path)
{
    int[] numbers = File.ReadLines(path).Select(int.Parse).ToArray();
    int t = Environment.ProcessorCount;
    for (int run = 1; run <= 3; run++) Run($"1b-v1-run{run}", t, () => SharedNoLock(numbers, t));
    Run("1b-v2", t, () => SharedWithLock(numbers, t));
    Run("1b-v3", t, () => PerThread(numbers, t));
}

static long[] SharedNoLock(int[] a, int threads)
{
    var c = new long[3];
    RunThreads(threads, i =>
    {
        int from = Start(a.Length, i, threads), to = Start(a.Length, i + 1, threads);
        for (int j = from; j < to; j++)
            c[Sign(a[j])]++;
    });
    return c;
}

static long[] SharedWithLock(int[] a, int threads)
{
    var c = new long[3];
    var sync = new object();
    RunThreads(threads, i =>
    {
        int from = Start(a.Length, i, threads), to = Start(a.Length, i + 1, threads);
        for (int j = from; j < to; j++)
            lock (sync) c[Sign(a[j])]++;
    });
    return c;
}

static long[] PerThread(int[] a, int threads)
{
    var results = new long[threads][];
    RunThreads(threads, i =>
    {
        var mine = new long[3];
        int from = Start(a.Length, i, threads), to = Start(a.Length, i + 1, threads);
        for (int j = from; j < to; j++)
            mine[Sign(a[j])]++;
        results[i] = mine;
    });
    return Sum(results);
}

static long[] Task1c(string path, int workers)
{
    var queue = new BlockingCollection<List<string>>(boundedCapacity: 100);
    var reader = new Thread(() =>
    {
        using var r = new StreamReader(path);
        var batch = new List<string>(10_000);
        string? line;
        while ((line = r.ReadLine()) != null)
        {
            batch.Add(line);
            if (batch.Count == 10_000) { queue.Add(batch); batch = new List<string>(10_000); }
        }
        if (batch.Count > 0) queue.Add(batch);
        queue.CompleteAdding();
    });
    reader.Start();

    var results = new long[workers][];
    RunThreads(workers, i =>
    {
        var mine = new long[3];
        foreach (var batch in queue.GetConsumingEnumerable())
            foreach (string s in batch) mine[Sign(int.Parse(s))]++;
        results[i] = mine;
    });
    reader.Join();
    return Sum(results);
}

static long[] Task2(string dir, int threads)
{
    string[] files = Directory.GetFiles(dir, "numbers_*.txt").Order().ToArray();
    var results = new long[threads][];
    RunThreads(threads, i =>
    {
        var counter = new SignCounter();
        for (int f = i; f < files.Length; f += threads)
        {
            byte[] data = File.ReadAllBytes(files[f]);
            counter.Feed(data);
            counter.End();
        }
        results[i] = counter.Counts;
    });
    return Sum(results);
}

static void RunThreads(int count, Action<int> body)
{
    var threads = new Thread[count];
    for (int i = 0; i < count; i++)
    {
        int id = i;
        threads[i] = new Thread(() => body(id));
        threads[i].Start();
    }
    foreach (var t in threads) t.Join();
}

static void Run(string name, int threads, Func<long[]> work)
{
    var sw = Stopwatch.StartNew();
    long[] c = work();
    sw.Stop();
    long peakMb = Process.GetCurrentProcess().PeakWorkingSet64 / (1024 * 1024);
    long total = c[0] + c[1] + c[2];
    Console.WriteLine($"{name} threads={threads} time_ms={sw.ElapsedMilliseconds} " +
                      $"neg={c[0]} zero={c[1]} pos={c[2]} total={total} peak_mb={peakMb}" +
                      (total == Total ? "" : " WRONG"));
}

static int Start(int length, int part, int parts) => (int)((long)length * part / parts);

static int Sign(int x) => x < 0 ? 0 : x == 0 ? 1 : 2;

static long[] Sum(long[][] parts)
{
    var c = new long[3];
    foreach (var p in parts)
        for (int k = 0; k < 3; k++) c[k] += p[k];
    return c;
}

class SignCounter
{
    public long[] Counts = new long[3];
    int value;
    bool minus, digits;

    public void Feed(ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            if (b == '\n') End();
            else if (b == '-') minus = true;
            else if (b >= '0' && b <= '9') { value = value * 10 + (b - '0'); digits = true; }
        }
    }

    public void End()
    {
        if (!digits) return;
        int x = minus ? -value : value;
        Counts[x < 0 ? 0 : x == 0 ? 1 : 2]++;
        value = 0; minus = false; digits = false;
    }
}
