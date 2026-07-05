using System;
using System.IO;

namespace MotoRouteFinder.Helpers;

public static class MemoryDiagnostics
{
    public static string Describe()
    {
        try
        {
            var totalMem = GC.GetTotalMemory(false);
            var totalAlloc = GC.GetTotalAllocatedBytes(false);
            var gcInfo = GC.GetGCMemoryInfo();
            var gen0 = gcInfo.GenerationInfo[0];
            var gen1 = gcInfo.GenerationInfo[1];
            var gen2 = gcInfo.GenerationInfo[2];
            var loh = gcInfo.GenerationInfo[3];
            var rssKB = GetRssKB();

            return $"RSS={rssKB}KB, Managed={totalMem / 1048576.0:F1}MB, " +
                   $"TotalAlloc={totalAlloc / 1048576.0:F1}MB, " +
                   $"HeapSize={gcInfo.HeapSizeBytes / 1048576.0:F1}MB, " +
                   $"Fragmented={gcInfo.FragmentedBytes / 1048576.0:F1}MB, " +
                   $"Gen0={gen0.SizeBeforeBytes / 1048576.0:F1}MB, " +
                   $"Gen1={gen1.SizeBeforeBytes / 1048576.0:F1}MB, " +
                   $"Gen2={gen2.SizeBeforeBytes / 1048576.0:F1}MB, " +
                   $"LOH={loh.SizeBeforeBytes / 1048576.0:F1}MB, " +
                   $"Compacted={gcInfo.Compacted}";
        }
        catch (Exception ex)
        {
            return $"[diag error: {ex.Message}]";
        }
    }

    public static string GetGCInfo()
    {
        try
        {
            var info = GC.GetGCMemoryInfo();
            return $"HeapSize={info.HeapSizeBytes / 1048576.0:F1}MB, Fragmented={info.FragmentedBytes / 1048576.0:F1}MB, TotalCommitted={info.TotalCommittedBytes / 1048576.0:F1}MB";
        }
        catch
        {
            return "[gc info unavailable]";
        }
    }

    public static long GetRssKB()
    {
        try
        {
            if (!OperatingSystem.IsLinux()) return 0;
            var status = File.ReadAllText("/proc/self/status");
            foreach (var line in status.Split('\n'))
            {
                if (line.StartsWith("VmRSS:", StringComparison.Ordinal))
                {
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && long.TryParse(parts[1], out var kb))
                        return kb;
                }
            }
        }
        catch { }
        return 0;
    }
}
