using System.CommandLine;

public record ProgressIndicator(IConsole console, Verbosity verbosity, double frequencySeconds = 1)
{
    private List<Progress> progressList = new List<Progress>();
    private int skipped = 0;
    public IProgress<long> NewProgress(long Total)
    {
        if (Total < 0)
        {
            skipped += 1;
            Total = 0;
        }
        var progress = new Progress(Signal, Total);
        lock (this)
        {
            progressList.Add(progress);
            if (startTime == DateTime.MinValue) { startTime = DateTime.Now; }
        }
        return progress;
    }

    private DateTime startTime = DateTime.MinValue;
    private DateTime lastUpdate = DateTime.MinValue;
    private void Signal()
    {
        if (verbosity < Verbosity.Normal) return;
        var now = DateTime.Now;
        if ((now - lastUpdate).TotalSeconds > frequencySeconds)
            lock (this)
            {
                lastUpdate = now;
                int count = 0;
                long value = 0;
                long total = 0;
                foreach (var progress in progressList)
                {
                    value += progress.Value;
                    total += Math.Max(progress.Total, progress.Value);
                    if (progress.Value >= progress.Total) count += 1;
                }
                var elapsed = (now - startTime).TotalSeconds;
                var estimated = value == 0 ? elapsed : elapsed * total / value;
                var eta = TimeSpan.FromSeconds(Math.Round(estimated - elapsed));
                console.Write($"Files: {count - skipped} done {skipped} skipped of {progressList.Count}, {fmt(value)} of {fmt(total)}, ETA {eta:g}                    \r");
            }
    }

    static string fmt(long bytes)
    {
        const double KB = 1024;
        const double MB = 1024 * KB;
        const double GB = 1024 * MB;
        if (bytes > 100 * GB) return $"{bytes / GB:F0} GB";
        if (bytes > GB) return $"{bytes / GB:F2} GB";
        if (bytes > 100 * MB) return $"{bytes / MB:F0} MB";
        if (bytes > MB) return $"{bytes / MB:F2} MB";
        if (bytes > 100 * KB) return $"{bytes / KB:F0} KB";
        if (bytes > KB) return $"{bytes / KB:F2} KB";
        return $"{bytes} B";
    }
    record Progress(Action Signal, long Total) : IProgress<long>
    {
        public long Value { get; private set; } = 0;

        public void Report(long value)
        {
            Value = value;
            Signal();
        }
    }

    public void Flush()
    {
        long total;
        lock (this)
        {
            total = progressList.Sum(progress => Math.Max(progress.Total, progress.Value));
        }
        var elapsed = startTime == DateTime.MinValue ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Round((DateTime.Now - startTime).TotalSeconds));
        var strSkipped = skipped > 0 ? $", skipped {skipped}" : "";
        console.WriteLine($"{progressList.Count} files, {fmt(total)} in {elapsed:g}{strSkipped}                         ");
        lock (this)
        {
            progressList.Clear();
        }
    }
}
