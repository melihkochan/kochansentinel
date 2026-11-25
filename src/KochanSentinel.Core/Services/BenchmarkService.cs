using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KochanSentinel.Core.Interop;
using KochanSentinel.Core.Models;

namespace KochanSentinel.Core.Services;

public sealed class BenchmarkService
{
    private static readonly string HistoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "KochanSentinel", "benchmark_history.json");

    public async Task<BenchmarkResult> RunFullBenchmarkAsync(
        string targetDrive,
        string cpuName,
        string gpuName,
        double totalRamGb,
        IProgress<BenchmarkProgress>? progress = null,
        CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var result = new BenchmarkResult
            {
                CpuName = cpuName,
                GpuName = gpuName,
                TotalRamGb = totalRamGb,
                TargetDrive = string.IsNullOrWhiteSpace(targetDrive) ? @"C:\" : targetDrive
            };

            // Stage 1: CPU Single-Core
            progress?.Report(new BenchmarkProgress
            {
                StageName = "1/4 • CPU Tek Çekirdek Testi",
                Detail = "SHA-256 + Kayan Nokta & Bit İşlem hızı ölçülüyor...",
                PercentComplete = 12
            });
            double singleMops = RunCpuSingleCore(ct);
            result.CpuSingleMops = Math.Round(singleMops, 1);
            // Reference 100 pts ~ midrange 3.6GHz desktop core (~295 Mops/s -> avoids hitting artificial ceilings on modern CPUs)
            result.CpuSingleScore = Math.Clamp((int)Math.Round((singleMops / 295.0) * 100.0), 10, 1000);

            // Stage 2: CPU Multi-Core
            ct.ThrowIfCancellationRequested();
            progress?.Report(new BenchmarkProgress
            {
                StageName = $"2/4 • CPU Çok Çekirdek Testi ({Environment.ProcessorCount} Thread)",
                Detail = "Tüm mantıksal çekirdekler paralel stres altında ölçülüyor...",
                PercentComplete = 38
            });
            double multiMops = RunCpuMultiCore(ct);
            result.CpuMultiMops = Math.Round(multiMops, 1);
            result.CpuScalingRatio = singleMops > 0.1 ? Math.Round(multiMops / singleMops, 1) : 1.0;
            // Reference 100 pts ~ 6c desktop (~1450 Mops/s)
            result.CpuMultiScore = Math.Clamp((int)Math.Round((multiMops / 1450.0) * 100.0), 10, 1500);

            // Stage 3: RAM Bandwidth & Latency
            ct.ThrowIfCancellationRequested();
            progress?.Report(new BenchmarkProgress
            {
                StageName = "3/4 • RAM Bant Genişliği & Gecikme Testi",
                Detail = "192 MB sıralı transfer (GB/s) ve 64 MB L3-üstü gecikme (ns) ölçülüyor...",
                PercentComplete = 65
            });
            var (ramGbps, ramNs) = RunMemoryBenchmark(ct);
            result.RamBandwidthGbPerSec = Math.Round(ramGbps, 1);
            result.RamLatencyNs = Math.Round(ramNs, 1);

            double bwRatio = ramGbps / 32.0; // Reference DDR4 dual-channel ~32 GB/s R+W
            double latRatio = ramNs > 10 ? 72.0 / ramNs : 1.0;
            result.RamScore = Math.Clamp((int)Math.Round((bwRatio * 0.65 + latRatio * 0.35) * 100.0), 10, 400);

            // Stage 4: SSD / NVMe Storage Benchmark (Unbuffered Direct I/O)
            ct.ThrowIfCancellationRequested();
            progress?.Report(new BenchmarkProgress
            {
                StageName = $"4/4 • Disk Hız Testi ({result.TargetDrive})",
                Detail = "Önbelleksiz (NO_BUFFERING) Sıralı Okuma/Yazma & 4K IOPS ölçülüyor...",
                PercentComplete = 85
            });
            var (seqRead, seqWrite, r4kIops, w4kIops) = RunDiskBenchmark(result.TargetDrive, ct);
            result.DiskSeqReadMbps = Math.Round(seqRead, 0);
            result.DiskSeqWriteMbps = Math.Round(seqWrite, 0);
            result.DiskRandom4kReadIops = r4kIops;
            result.DiskRandom4kWriteIops = w4kIops;

            double seqRatio = (seqRead / 2200.0) * 0.5 + (seqWrite / 1600.0) * 0.5;
            double iopsRatio = (r4kIops / 25000.0) * 0.5 + (w4kIops / 22000.0) * 0.5;
            result.DiskScore = Math.Clamp((int)Math.Round((seqRatio * 0.45 + iopsRatio * 0.55) * 100.0), 5, 500);

            // UserBenchmark-style Weighted Profiles
            result.GamingPercent = Math.Clamp((int)Math.Round(
                result.CpuSingleScore * 0.58 + result.RamScore * 0.24 + result.DiskScore * 0.18), 10, 450);
            result.DesktopPercent = Math.Clamp((int)Math.Round(
                result.CpuSingleScore * 0.42 + result.DiskScore * 0.40 + result.RamScore * 0.18), 10, 450);
            result.WorkstationPercent = Math.Clamp((int)Math.Round(
                result.CpuMultiScore * 0.56 + result.RamScore * 0.24 + result.DiskScore * 0.20), 10, 450);
            result.OverallScore = (int)Math.Round(
                (result.GamingPercent + result.DesktopPercent + result.WorkstationPercent) / 3.0);

            result.TierBadge = result.OverallScore switch
            {
                >= 160 => "Elit / Enthusiast",
                >= 120 => "Yüksek Performans",
                >= 85 => "Dengeli / İyi",
                >= 55 => "Giriş / Standart",
                _ => "Darboğaz / Bakım Önerilir"
            };

            result.BottleneckNote = BuildBottleneckAnalysis(result);

            progress?.Report(new BenchmarkProgress
            {
                StageName = "Tamamlandı",
                Detail = $"Genel Sistem Puanı: %{result.OverallScore} ({result.TierBadge})",
                PercentComplete = 100
            });

            SaveHistoryEntry(result);
            return result;
        }, ct);
    }

    private static double RunCpuSingleCore(CancellationToken ct)
    {
        // Warmup JIT
        ExecuteCpuKernel(120);

        const int durationMs = 1100;
        var sw = Stopwatch.StartNew();
        long ops = 0;
        while (sw.ElapsedMilliseconds < durationMs)
        {
            ct.ThrowIfCancellationRequested();
            ops += ExecuteCpuKernel(400);
        }
        sw.Stop();
        return ops / sw.Elapsed.TotalSeconds / 1_000_000.0;
    }

    private static double RunCpuMultiCore(CancellationToken ct)
    {
        int threads = Math.Max(1, Environment.ProcessorCount);
        const int durationMs = 1350;
        long totalOps = 0;

        using var startGate = new ManualResetEventSlim(false);
        var tasks = new Task[threads];
        for (int t = 0; t < threads; t++)
        {
            tasks[t] = Task.Factory.StartNew(() =>
            {
                ExecuteCpuKernel(80);
                startGate.Wait(ct);
                var localSw = Stopwatch.StartNew();
                long localOps = 0;
                while (localSw.ElapsedMilliseconds < durationMs)
                {
                    if (ct.IsCancellationRequested) break;
                    localOps += ExecuteCpuKernel(400);
                }
                Interlocked.Add(ref totalOps, localOps);
            }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        var sw = Stopwatch.StartNew();
        startGate.Set();
        Task.WaitAll(tasks, ct);
        sw.Stop();
        ct.ThrowIfCancellationRequested();
        return totalOps / sw.Elapsed.TotalSeconds / 1_000_000.0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long ExecuteCpuKernel(int batches)
    {
        Span<byte> payload = stackalloc byte[512];
        Span<byte> digest = stackalloc byte[32];
        double fp = 1.000137;
        ulong bits = 0x9E3779B97F4A7C15UL;

        for (int b = 0; b < batches; b++)
        {
            for (int i = 0; i < 64; i++)
            {
                fp = Math.FusedMultiplyAdd(fp, 1.00000017, 0.0000013);
                bits = BitOperations.RotateLeft(bits ^ (ulong)b, 13) * 0xBF58476D1CE4E5B9UL;
            }
            payload[0] = (byte)bits;
            payload[63] = (byte)(bits >> 24);
            SHA256.HashData(payload, digest);
            bits ^= MemoryMarshal.Read<ulong>(digest);
        }

        // Prevent dead-code elimination while costing 1 cycle
        if (fp < -999999 && bits == 123456) return 0;
        return batches * 250L;
    }

    private static (double BandwidthGbPerSec, double LatencyNs) RunMemoryBenchmark(CancellationToken ct)
    {
        // 1. Sequential Bandwidth over 192 MB arrays (exceeds CPU L3 caches)
        const int arrayBytes = 192 * 1024 * 1024;
        var src = GC.AllocateUninitializedArray<byte>(arrayBytes);
        var dst = GC.AllocateUninitializedArray<byte>(arrayBytes);
        for (int i = 0; i < arrayBytes; i += 4096) src[i] = (byte)(i & 0xFF);

        Buffer.BlockCopy(src, 0, dst, 0, arrayBytes); // Warmup page fault commit

        const int bwDurationMs = 950;
        var sw = Stopwatch.StartNew();
        long passes = 0;
        while (sw.ElapsedMilliseconds < bwDurationMs)
        {
            ct.ThrowIfCancellationRequested();
            Buffer.BlockCopy(src, 0, dst, 0, arrayBytes);
            passes++;
        }
        sw.Stop();
        // Copy moves 2x buffer bytes across memory bus (Read + Write)
        double totalGb = passes * (arrayBytes * 2.0) / 1_000_000_000.0;
        double gbps = totalGb / sw.Elapsed.TotalSeconds;

        // Release 384MB before allocating pointer-chase array
        src = null!;
        dst = null!;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);

        // 2. Random Pointer-Chase Latency over 64 MB buffer (16M ints -> Sattolo single cycle)
        ct.ThrowIfCancellationRequested();
        const int count = 16 * 1024 * 1024;
        var chain = GC.AllocateUninitializedArray<int>(count);
        var order = GC.AllocateUninitializedArray<int>(count);
        for (int i = 0; i < count; i++) order[i] = i;

        var rng = Random.Shared;
        for (int i = count - 1; i > 0; i--)
        {
            int j = rng.Next(0, i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }
        for (int i = 0; i < count - 1; i++)
            chain[order[i]] = order[i + 1];
        chain[order[count - 1]] = order[0];
        order = null!;

        // Warmup TLB & run 3,000,000 dependent hops
        RunLatencyChase(chain, 200_000);
        const int hops = 3_000_000;
        var swLat = Stopwatch.StartNew();
        int sink = RunLatencyChase(chain, hops);
        swLat.Stop();
        GC.KeepAlive(sink);

        double latencyNs = swLat.Elapsed.TotalNanoseconds / hops;
        return (gbps, latencyNs);
    }

    [MethodImpl(MethodImplOptions.NoOptimization | MethodImplOptions.NoInlining)]
    private static int RunLatencyChase(int[] chain, int steps)
    {
        int idx = 0;
        for (int i = 0; i < steps; i++)
            idx = chain[idx];
        return idx;
    }

    private static (double SeqReadMbps, double SeqWriteMbps, int RandReadIops, int RandWriteIops) RunDiskBenchmark(string driveRoot, CancellationToken ct)
    {
        string testDir = driveRoot;
        if (!Directory.Exists(testDir)) testDir = @"C:\";

        // Verify write permissions cleanly; fallback to %TEMP% if root ACL blocks write
        string probe = Path.Combine(testDir, $"_kochan_bench_{Environment.ProcessId}.tmp");
        try
        {
            using (File.Create(probe, 4096, FileOptions.DeleteOnClose)) { }
        }
        catch
        {
            testDir = Path.GetTempPath();
            probe = Path.Combine(testDir, $"_kochan_bench_{Environment.ProcessId}.tmp");
        }

        const uint seqChunkSize = 1024 * 1024; // 1 MB
        const int seqChunkCount = 128;         // 128 MB test file
        const uint randBlockSize = 4096;       // 4 KB aligned
        const int randReadOps = 1200;
        const int randWriteOps = 800;

        IntPtr rawMem = Marshal.AllocHGlobal((int)seqChunkSize + 4096);
        IntPtr alignedBuf = (IntPtr)(((long)rawMem + 4095) & ~4095L);

        try
        {
            // Fill unmanaged buffer deterministically
            byte[] seed = new byte[4096];
            Random.Shared.NextBytes(seed);
            for (int off = 0; off < seqChunkSize; off += 4096)
                Marshal.Copy(seed, 0, IntPtr.Add(alignedBuf, off), 4096);

            // 1. Sequential Write (NO_BUFFERING | WRITE_THROUGH)
            double seqWriteMbps;
            using (var handle = NativeMethods.CreateFile(
                probe,
                NativeMethods.GENERIC_WRITE,
                0,
                IntPtr.Zero,
                NativeMethods.CREATE_ALWAYS,
                NativeMethods.FILE_FLAG_NO_BUFFERING | NativeMethods.FILE_FLAG_WRITE_THROUGH,
                IntPtr.Zero))
            {
                if (handle.IsInvalid)
                    return RunManagedFallbackDiskBenchmark(probe, ct);

                var sw = Stopwatch.StartNew();
                for (int i = 0; i < seqChunkCount; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!NativeMethods.WriteFile(handle, alignedBuf, seqChunkSize, out _, IntPtr.Zero))
                        break;
                }
                NativeMethods.FlushFileBuffers(handle);
                sw.Stop();
                seqWriteMbps = seqChunkCount / sw.Elapsed.TotalSeconds;
            }

            // 2. Sequential Read (NO_BUFFERING)
            double seqReadMbps;
            using (var handle = NativeMethods.CreateFile(
                probe,
                NativeMethods.GENERIC_READ,
                0,
                IntPtr.Zero,
                NativeMethods.OPEN_EXISTING,
                NativeMethods.FILE_FLAG_NO_BUFFERING,
                IntPtr.Zero))
            {
                if (handle.IsInvalid)
                    return (0, seqWriteMbps, 0, 0);

                var sw = Stopwatch.StartNew();
                for (int i = 0; i < seqChunkCount; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!NativeMethods.ReadFile(handle, alignedBuf, seqChunkSize, out uint read, IntPtr.Zero) || read == 0)
                        break;
                }
                sw.Stop();
                seqReadMbps = seqChunkCount / sw.Elapsed.TotalSeconds;
            }

            // 3. Random 4K Read & Write IOPS (NO_BUFFERING)
            long maxBlockIdx = ((long)seqChunkSize * seqChunkCount) / randBlockSize;
            int rIops = 0, wIops = 0;
            using (var handle = NativeMethods.CreateFile(
                probe,
                NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
                0,
                IntPtr.Zero,
                NativeMethods.OPEN_EXISTING,
                NativeMethods.FILE_FLAG_NO_BUFFERING | NativeMethods.FILE_FLAG_WRITE_THROUGH,
                IntPtr.Zero))
            {
                if (!handle.IsInvalid)
                {
                    var rng = Random.Shared;
                    var swR = Stopwatch.StartNew();
                    int doneR = 0;
                    for (int i = 0; i < randReadOps; i++)
                    {
                        if (i % 100 == 0) ct.ThrowIfCancellationRequested();
                        long offset = rng.NextInt64(0, maxBlockIdx) * randBlockSize;
                        NativeMethods.SetFilePointerEx(handle, offset, out _, NativeMethods.FILE_BEGIN);
                        if (NativeMethods.ReadFile(handle, alignedBuf, randBlockSize, out _, IntPtr.Zero))
                            doneR++;
                    }
                    swR.Stop();
                    rIops = swR.Elapsed.TotalSeconds > 0 ? (int)Math.Round(doneR / swR.Elapsed.TotalSeconds) : 0;

                    var swW = Stopwatch.StartNew();
                    int doneW = 0;
                    for (int i = 0; i < randWriteOps; i++)
                    {
                        if (i % 100 == 0) ct.ThrowIfCancellationRequested();
                        long offset = rng.NextInt64(0, maxBlockIdx) * randBlockSize;
                        NativeMethods.SetFilePointerEx(handle, offset, out _, NativeMethods.FILE_BEGIN);
                        if (NativeMethods.WriteFile(handle, alignedBuf, randBlockSize, out _, IntPtr.Zero))
                            doneW++;
                    }
                    NativeMethods.FlushFileBuffers(handle);
                    swW.Stop();
                    wIops = swW.Elapsed.TotalSeconds > 0 ? (int)Math.Round(doneW / swW.Elapsed.TotalSeconds) : 0;
                }
            }

            return (seqReadMbps, seqWriteMbps, rIops, wIops);
        }
        finally
        {
            Marshal.FreeHGlobal(rawMem);
            try { if (File.Exists(probe)) File.Delete(probe); } catch { }
        }
    }

    private static (double, double, int, int) RunManagedFallbackDiskBenchmark(string probe, CancellationToken ct)
    {
        const int totalMb = 96;
        byte[] chunk = new byte[1024 * 1024];
        Random.Shared.NextBytes(chunk);
        try
        {
            var swW = Stopwatch.StartNew();
            using (var fs = new FileStream(probe, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                for (int i = 0; i < totalMb; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    fs.Write(chunk, 0, chunk.Length);
                }
                fs.Flush(true);
            }
            swW.Stop();

            var swR = Stopwatch.StartNew();
            using (var fs = new FileStream(probe, FileMode.Open, FileAccess.Read, FileShare.None, 4096))
            {
                while (fs.Read(chunk, 0, chunk.Length) > 0)
                    ct.ThrowIfCancellationRequested();
            }
            swR.Stop();

            return (totalMb / swR.Elapsed.TotalSeconds, totalMb / swW.Elapsed.TotalSeconds, 0, 0);
        }
        finally
        {
            try { if (File.Exists(probe)) File.Delete(probe); } catch { }
        }
    }

    private static string BuildBottleneckAnalysis(BenchmarkResult r)
    {
        var scores = new[]
        {
            ("CPU Tek Çekirdek", r.CpuSingleScore),
            ("CPU Çok Çekirdek", r.CpuMultiScore),
            ("RAM Bellek", r.RamScore),
            ("Depolama / SSD", r.DiskScore)
        };

        var weakest = scores.OrderBy(x => x.Item2).First();
        var strongest = scores.OrderByDescending(x => x.Item2).First();

        if (r.DiskScore < 45)
            return $"En zayıf halka: {weakest.Item1} ({weakest.Item2} puan). SATA HDD/yavaş disk oyun yükleme sürelerini uzatıyor olabilir; NVMe SSD yükseltmesi büyük fark yaratır.";
        if (r.RamLatencyNs > 95 || r.RamBandwidthGbPerSec < 18)
            return $"Dikkat: RAM gecikmesi yüksek ({r.RamLatencyNs:F0} ns) veya tek kanal çalışıyor olabilir ({r.RamBandwidthGbPerSec:F1} GB/s). XMP/EXPO profilini BIOS'tan kontrol edebilirsin.";
        return $"En güçlü bileşen: {strongest.Item1} ({strongest.Item2} puan) • Bileşenler arası dağılım dengeli ({weakest.Item1}: {weakest.Item2} puan).";
    }

    public List<BenchmarkResult> LoadHistory()
    {
        try
        {
            if (File.Exists(HistoryPath))
            {
                var list = JsonSerializer.Deserialize<List<BenchmarkResult>>(File.ReadAllText(HistoryPath));
                if (list != null) return list;
            }
        }
        catch { }
        return new List<BenchmarkResult>();
    }

    public void SaveHistoryEntry(BenchmarkResult entry)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
            var list = LoadHistory();
            list.Insert(0, entry);
            var trimmed = list.Take(20).ToList();
            File.WriteAllText(HistoryPath, JsonSerializer.Serialize(trimmed, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public void ClearHistory()
    {
        try
        {
            if (File.Exists(HistoryPath))
                File.Delete(HistoryPath);
        }
        catch { }
    }
}
