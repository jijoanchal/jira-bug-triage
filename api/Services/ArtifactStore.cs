using System.IO.Compression;

namespace Triage.Api.Services;

/// <summary>
/// Holds artifacts uploaded alongside the report JSON and stores them on disk.
/// Playwright records artifact paths from the machine that ran the tests, so paths are matched by their
/// longest common trailing path segments (e.g. "test-results/login-chromium/test-failed-1.png").
/// </summary>
public class ArtifactStore(IConfiguration cfg, IntegrationConfig integ)
{
    public string DataDir { get; } = Path.GetFullPath(cfg["DataDir"] ?? "App_Data");

    public string Abs(string relative) => Path.Combine(DataDir, relative);

    /// <summary>Uploaded loose files and zip entries, keyed by relative path as supplied by the browser.</summary>
    public class Pool
    {
        public List<(string Path, byte[] Data)> Items { get; } = new();
    }

    public async Task<Pool> BuildPoolAsync(IEnumerable<IFormFile> files)
    {
        var pool = new Pool();
        foreach (var f in files)
        {
            if (f.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                await using var s = f.OpenReadStream();
                using var zip = new ZipArchive(s, ZipArchiveMode.Read);
                foreach (var e in zip.Entries.Where(e => e.Length > 0))
                {
                    await using var es = e.Open();
                    using var ms = new MemoryStream();
                    await es.CopyToAsync(ms);
                    pool.Items.Add((Normalize(e.FullName), ms.ToArray()));
                }
            }
            else
            {
                using var ms = new MemoryStream();
                await f.CopyToAsync(ms);
                pool.Items.Add((Normalize(f.FileName), ms.ToArray()));
            }
        }
        return pool;
    }

    /// <summary>Finds the bytes for an artifact path recorded in the report, or null.</summary>
    public byte[]? Resolve(string reportedPath, Pool pool)
    {
        var want = Segments(reportedPath);
        if (want.Length == 0) return null;

        (string Path, byte[] Data)? best = null;
        var bestScore = 0;
        foreach (var item in pool.Items)
        {
            var have = Segments(item.Path);
            var score = 0;
            while (score < want.Length && score < have.Length &&
                   string.Equals(want[^(score + 1)], have[^(score + 1)], StringComparison.OrdinalIgnoreCase)) score++;
            if (score > bestScore) { bestScore = score; best = item; }
        }
        if (best != null) return best.Value.Data;

        // Optional server-side artifact folder. Only files inside the configured root are ever read.
        if (!string.IsNullOrEmpty(integ.ArtifactsRoot) && Directory.Exists(integ.ArtifactsRoot))
        {
            var root = Path.GetFullPath(integ.ArtifactsRoot);
            for (var skip = 0; skip < want.Length; skip++)
            {
                var rel = Path.Combine(want[skip..]);
                var full = Path.GetFullPath(Path.Combine(root, rel));
                if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(full))
                    return File.ReadAllBytes(full);
            }
        }
        return null;
    }

    public async Task<string> SaveAsync(int runId, int failureId, string fileName, byte[] data)
    {
        var safe = string.Concat(fileName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var rel = Path.Combine("uploads", $"run-{runId}", $"failure-{failureId}", safe);
        var abs = Abs(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        await File.WriteAllBytesAsync(abs, data);
        return rel;
    }

    private static string Normalize(string p) => p.Replace('\\', '/');

    private static string[] Segments(string p) =>
        Normalize(p).Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(s => s != "." && s != ".." && !(s.Length == 2 && s[1] == ':')).ToArray();
}
