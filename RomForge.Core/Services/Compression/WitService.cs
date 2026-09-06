using Common;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace RomForge.Core.Services.Compression;

public static class WitService
{
    private const string ToolDirectoryName = "wit-v3.05a-r8638-cygwin64";
    private const string ResourceSuffix = ".Assets.Tools.wit-v3.05a-r8638-cygwin64.zip";
    private static readonly object SyncRoot = new();

    public static async Task ConvertAsync(string inputPath, string outputPath, string outputFormat, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        string executablePath = EnsureExecutable();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executablePath)
            {
                WorkingDirectory = Path.GetDirectoryName(executablePath)!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.StartInfo.ArgumentList.Add("copy");
        process.StartInfo.ArgumentList.Add("--progress");
        process.StartInfo.ArgumentList.Add(inputPath);
        process.StartInfo.ArgumentList.Add(outputPath);
        process.StartInfo.ArgumentList.Add(outputFormat.ToLowerInvariant() switch
        {
            "wbfs" => "--wbfs",
            "gcz" => "--gcz",
            _ => "--iso"
        });

        process.Start();
        Task<string> outputTask = ReadStreamAsync(process.StandardOutput, progress, ct);
        Task<string> errorTask = ReadStreamAsync(process.StandardError, progress, ct);

        using var registration = ct.Register(() =>
        {
            if (!process.HasExited)
                process.Kill(true);
        });

        await process.WaitForExitAsync(ct);
        string output = await outputTask;
        string error = await errorTask;

        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? output : error);

        progress?.Report(new ProgressInfo { Percent = 100 });
    }

    private static string EnsureExecutable()
    {
        string toolDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RomForge", "Tools", ToolDirectoryName);
        string executablePath = Path.Combine(toolDirectory, "wit.exe");

        if (File.Exists(executablePath))
            return executablePath;

        lock (SyncRoot)
        {
            if (File.Exists(executablePath))
                return executablePath;

            Directory.CreateDirectory(toolDirectory);
            string resourceName = Assembly.GetExecutingAssembly().GetManifestResourceNames()
                .Single(name => name.EndsWith(ResourceSuffix, StringComparison.Ordinal));

            using Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)!;
            using var archive = new ZipArchive(resource, ZipArchiveMode.Read);

            foreach (var entry in archive.Entries)
            {
                int binIndex = entry.FullName.IndexOf("/bin/", StringComparison.OrdinalIgnoreCase);

                if (binIndex < 0 || string.IsNullOrEmpty(entry.Name))
                    continue;

                string destinationPath = Path.Combine(toolDirectory, entry.FullName[(binIndex + 5)..]);
                string destinationDirectory = Path.GetDirectoryName(destinationPath)!;
                Directory.CreateDirectory(destinationDirectory);

                using Stream input = entry.Open();
                using FileStream output = new(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
                input.CopyTo(output);
            }
        }

        return executablePath;
    }

    private static async Task<string> ReadStreamAsync(StreamReader reader, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var result = new StringBuilder();
        char[] buffer = new char[256];

        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(), ct);

            if (read == 0)
                return result.ToString();

            string chunk = new(buffer, 0, read);
            result.Append(chunk);

            foreach (Match match in Regex.Matches(chunk, @"(?<!\d)(\d{1,3})(?:\.\d+)?%"))
                progress?.Report(new ProgressInfo { Percent = Math.Min(100, int.Parse(match.Groups[1].Value)) });
        }
    }
}
