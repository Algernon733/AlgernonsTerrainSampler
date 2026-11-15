#nullable enable
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace AlgernonsTerrainSampler.Tiles;

/// <summary>One tile being made: sampled column by column, then encoded and written to disk.</summary>
internal sealed class TerrainTileJob
{
    public readonly TerrainTileKey Key;
    public readonly TerrainTile Tile;
    public int Next;
    public double SampleMilliseconds;
    public byte[]? Result;
    public Exception? Error;

    public TerrainTileJob(TerrainTileKey key)
    {
        this.Key = key;
        this.Tile = new TerrainTile(key);
    }

    public bool Sampled => this.Next >= TerrainTileKey.ColumnCount;

    /// <summary>
    /// Samples columns until the tile is whole or <paramref name="budgetMilliseconds"/> have passed. Rows in order, so a
    /// row band's map regions and chunk contexts stay in the sampler's caches while it is crossed.
    /// </summary>
    public void Sample(Func<int, int, TerrainColumnSample> sample, int mapSizeX, int mapSizeZ, double budgetMilliseconds, Func<int> throttleMilliseconds, CancellationToken token)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (this.Next < TerrainTileKey.ColumnCount)
        {
            int ix = this.Next % TerrainTileKey.Samples;
            int iz = this.Next / TerrainTileKey.Samples;
            int x = Math.Clamp(this.Key.SampleX(ix), 0, mapSizeX - 1);
            int z = Math.Clamp(this.Key.SampleZ(iz), 0, mapSizeZ - 1);
            this.Tile.Set(this.Next, sample(x, z));
            this.Next++;

            if ((this.Next & 127) == 0)
            {
                token.ThrowIfCancellationRequested();
                if (timer.Elapsed.TotalMilliseconds >= budgetMilliseconds)
                    break;

                // Under load the worker gives way: a short pause every 1,024 columns.
                int pause = throttleMilliseconds();
                if (pause > 0 && (this.Next & 1023) == 0 && token.WaitHandle.WaitOne(pause))
                    token.ThrowIfCancellationRequested();
            }
        }

        this.SampleMilliseconds += timer.Elapsed.TotalMilliseconds;
    }
}

/// <summary>
/// The tile workers: persistent threads, so the sampler's thread-local contexts stay warm between tiles, at below-normal
/// priority, so the game's own threads come first.
/// </summary>
internal sealed class TerrainTileWorkers : IDisposable
{
    private readonly BlockingCollection<TerrainTileJob> queue = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly Thread[] threads;
    private readonly Action<TerrainTileJob, CancellationToken> run;
    private int disposed;

    public TerrainTileWorkers(int count, Action<TerrainTileJob, CancellationToken> run)
    {
        this.run = run;
        this.threads = new Thread[Math.Max(1, count)];
        for (int i = 0; i < this.threads.Length; i++)
        {
            this.threads[i] = new Thread(this.Run) { IsBackground = true, Name = $"Terrain Sampler tiles {i + 1}" };
            try
            {
                this.threads[i].Priority = ThreadPriority.BelowNormal;
            }
            catch (Exception ex) when (ex is PlatformNotSupportedException or ThreadStateException)
            {
                // Some platforms do not let a process lower a thread's priority; the throttle still applies.
            }

            this.threads[i].Start();
        }
    }

    public int Count => this.threads.Length;

    public CancellationToken Token => this.cancellation.Token;

    public void Submit(TerrainTileJob job) => this.queue.Add(job);

    private void Run()
    {
        try
        {
            foreach (TerrainTileJob job in this.queue.GetConsumingEnumerable(this.cancellation.Token))
                this.run(job, this.cancellation.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Cancels what is running and waits for every worker to stop: before the sampler disposes its caches.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
            return;

        this.cancellation.Cancel();
        this.queue.CompleteAdding();
        foreach (Thread thread in this.threads)
            thread.Join();

        this.queue.Dispose();
        this.cancellation.Dispose();
    }
}
