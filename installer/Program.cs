using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;

namespace EmotionBallDesktop.Setup;

internal static class Program
{
    private const string PayloadResourceName = "EmotionBallDesktop.Payload.zip";
    private const string SetupMutexName = @"Local\EmotionBallDesktopSetup";

    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        using var mutex = new Mutex(initiallyOwned: true, SetupMutexName, out var ownsMutex);
        if (!ownsMutex)
        {
            MessageBox.Show("安装程序已经在运行。", "EmotionBallDesktop", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        try
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
            var extractOnlyIndex = Array.IndexOf(args, "--extract-only");
            var extractOnly = extractOnlyIndex >= 0;
            var installDirectory = extractOnly && extractOnlyIndex + 1 < args.Length
                ? Path.GetFullPath(args[extractOnlyIndex + 1])
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EmotionBallDesktop", version);

            InstallPayload(installDirectory);
            if (extractOnly)
            {
                return 0;
            }

            var executable = Path.Combine(installDirectory, "EmotionBallDesktop.exe");
            if (!File.Exists(executable))
            {
                throw new FileNotFoundException("桌宠主程序没有从安装包中释放出来。", executable);
            }

            Process.Start(new ProcessStartInfo(executable)
            {
                WorkingDirectory = installDirectory,
                UseShellExecute = true,
            });
            return 0;
        }
        catch (Exception exception)
        {
            MessageBox.Show($"EmotionBallDesktop 启动失败：\n\n{exception.Message}", "EmotionBallDesktop", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private static void InstallPayload(string installDirectory)
    {
        using var hashStream = OpenPayload();
        var payloadHash = Convert.ToHexString(SHA256.HashData(hashStream));
        var markerPath = Path.Combine(installDirectory, ".payload.sha256");
        if (File.Exists(Path.Combine(installDirectory, "EmotionBallDesktop.exe")) &&
            File.Exists(markerPath) &&
            string.Equals(File.ReadAllText(markerPath).Trim(), payloadHash, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var parentDirectory = Directory.GetParent(installDirectory)?.FullName
            ?? throw new InvalidOperationException("无法确定安装目录。");
        Directory.CreateDirectory(parentDirectory);
        var stagingDirectory = Path.Combine(parentDirectory, $".staging-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);

        try
        {
            using var payloadStream = OpenPayload();
            using var archive = new ZipArchive(payloadStream, ZipArchiveMode.Read, leaveOpen: false);
            var prefix = FindCommonPackagePrefix(archive.Entries);
            var stagingPrefix = Path.GetFullPath(stagingDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

            foreach (var entry in archive.Entries)
            {
                var relativeName = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                if (prefix.Length > 0 && relativeName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    relativeName = relativeName[prefix.Length..];
                }
                if (string.IsNullOrWhiteSpace(relativeName))
                {
                    continue;
                }

                var destination = Path.GetFullPath(Path.Combine(stagingDirectory, relativeName));
                if (!destination.StartsWith(stagingPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("安装包包含不安全的文件路径。");
                }

                if (entry.FullName.EndsWith('/'))
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: true);
            }

            File.WriteAllText(Path.Combine(stagingDirectory, ".payload.sha256"), payloadHash);
            if (Directory.Exists(installDirectory))
            {
                Directory.Delete(installDirectory, recursive: true);
            }
            Directory.Move(stagingDirectory, installDirectory);
        }
        finally
        {
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
        }
    }

    private static Stream OpenPayload()
    {
        return Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResourceName)
            ?? throw new InvalidDataException("安装程序中缺少桌宠资源包。");
    }

    private static string FindCommonPackagePrefix(IReadOnlyCollection<ZipArchiveEntry> entries)
    {
        var firstSegments = entries
            .Select(entry => entry.FullName.Split('/', 2)[0])
            .Where(segment => !string.IsNullOrWhiteSpace(segment))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return firstSegments.Length == 1 ? firstSegments[0] + Path.DirectorySeparatorChar : string.Empty;
    }
}
