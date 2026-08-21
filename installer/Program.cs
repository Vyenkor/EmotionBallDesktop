using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;

namespace EmotionballDeskpet.Setup;

internal static class Program
{
    private const string PayloadResourceName = "Emotionball-Deskpet.Payload.zip";
    private const string SetupMutexName = @"Local\EmotionballDeskpetSetup";
    private const string PetExecutableName = "Emotionball-Deskpet.exe";
    private const string UninstallerExecutableName = "Emotionball-Deskpet-Uninstall.exe";

    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        using var mutex = new Mutex(initiallyOwned: true, SetupMutexName, out var ownsMutex);
        if (!ownsMutex)
        {
            MessageBox.Show("安装程序已经在运行。", "Emotionball-Deskpet", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        try
        {
            if (HasArgument(args, "--uninstall-worker"))
            {
                return RunUninstallWorker(args);
            }

            if (HasArgument(args, "--uninstall"))
            {
                return RunUninstaller(args);
            }

            if (string.Equals(Path.GetFileName(Environment.ProcessPath), UninstallerExecutableName, StringComparison.OrdinalIgnoreCase))
            {
                return RunUninstaller(args);
            }

            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
            var extractOnlyIndex = Array.IndexOf(args, "--extract-only");
            var extractOnly = extractOnlyIndex >= 0;
            var installDirectory = extractOnly && extractOnlyIndex + 1 < args.Length
                ? Path.GetFullPath(args[extractOnlyIndex + 1])
                : ChooseInstallDirectory(version);

            if (string.IsNullOrWhiteSpace(installDirectory))
            {
                return 0;
            }

            if (extractOnly && (Directory.Exists(installDirectory) || File.Exists(installDirectory)))
            {
                throw new IOException("--extract-only 目标必须是不存在的新目录，安装器不会删除已有目录。");
            }

            InstallPayload(installDirectory);
            InstallUninstaller(installDirectory);

            if (extractOnly)
            {
                return 0;
            }

            var executable = Path.Combine(installDirectory, PetExecutableName);
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
            MessageBox.Show($"Emotionball-Deskpet 启动失败：\n\n{exception.Message}", "Emotionball-Deskpet", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private static string? ChooseInstallDirectory(string version)
    {
        var defaultDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Emotionball-Deskpet",
            version);

        using var form = new InstallDirectoryForm(defaultDirectory);
        return form.ShowDialog() == DialogResult.OK ? form.InstallDirectory : null;
    }

    private static void InstallPayload(string installDirectory)
    {
        installDirectory = Path.GetFullPath(installDirectory);
        using var hashStream = OpenPayload();
        var payloadHash = Convert.ToHexString(SHA256.HashData(hashStream));
        var markerPath = Path.Combine(installDirectory, ".payload.sha256");
        if (File.Exists(Path.Combine(installDirectory, PetExecutableName)) &&
            File.Exists(markerPath) &&
            string.Equals(File.ReadAllText(markerPath).Trim(), payloadHash, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (Directory.Exists(installDirectory) &&
            !File.Exists(markerPath) &&
            Directory.EnumerateFileSystemEntries(installDirectory).Any())
        {
            throw new IOException("目标安装目录已存在且不为空。请选择空目录，或选择之前的 Emotionball-Deskpet 安装目录。");
        }

        StopInstalledPet(installDirectory);

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

    private static void InstallUninstaller(string installDirectory)
    {
        var source = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
        {
            throw new InvalidOperationException("无法定位安装程序自身，未能写入卸载程序。");
        }

        var destination = Path.Combine(installDirectory, UninstallerExecutableName);
        if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(source, destination, overwrite: true);
        }
    }

    private static int RunUninstaller(string[] args)
    {
        var installDirectory = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal)
            ? Path.GetFullPath(args[1])
            : Path.GetFullPath(AppContext.BaseDirectory);
        if (!IsValidInstallDirectory(installDirectory))
        {
            MessageBox.Show("无法确认这是 Emotionball-Deskpet 的安装目录。", "Emotionball-Deskpet", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        var confirm = MessageBox.Show(
            $"确定要卸载 Emotionball-Deskpet 吗？\n\n安装目录：{installDirectory}\n\n这会删除该目录中的桌宠程序和设置文件。",
            "卸载 Emotionball-Deskpet",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.Yes)
        {
            return 0;
        }

        StopInstalledPet(installDirectory);
        var workerPath = Path.Combine(Path.GetTempPath(), $"Emotionball-Deskpet-Uninstall-{Guid.NewGuid():N}.exe");
        var source = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位卸载程序自身。");
        File.Copy(source, workerPath, overwrite: false);

        var worker = new ProcessStartInfo(workerPath)
        {
            WorkingDirectory = Path.GetTempPath(),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        worker.ArgumentList.Add("--uninstall-worker");
        worker.ArgumentList.Add(installDirectory);
        Process.Start(worker);
        return 0;
    }

    private static int RunUninstallWorker(string[] args)
    {
        if (args.Length < 2)
        {
            return 1;
        }

        var installDirectory = Path.GetFullPath(args[1]);
        if (!IsValidInstallDirectory(installDirectory))
        {
            return 1;
        }

        StopInstalledPet(installDirectory);
        if (Directory.Exists(installDirectory))
        {
            Directory.Delete(installDirectory, recursive: true);
        }

        var workerPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(workerPath) && File.Exists(workerPath))
        {
            var cleanup = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            cleanup.ArgumentList.Add("/d");
            cleanup.ArgumentList.Add("/c");
            cleanup.ArgumentList.Add($"ping 127.0.0.1 -n 3 >nul & del /f /q \"{workerPath.Replace("\"", string.Empty)}\"");
            Process.Start(cleanup);
        }

        return 0;
    }

    private static void StopInstalledPet(string installDirectory)
    {
        var normalizedDirectory = Path.GetFullPath(installDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(PetExecutableName)))
        {
            try
            {
                var executablePath = process.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(executablePath) ||
                    !Path.GetFullPath(executablePath).StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                process.CloseMainWindow();
                if (!process.WaitForExit(1500))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(1500);
                }
            }
            catch (Exception) when (process.HasExited)
            {
                // The process exited while it was being inspected.
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static bool IsValidInstallDirectory(string directory)
    {
        var marker = Path.Combine(directory, ".payload.sha256");
        var executable = Path.Combine(directory, PetExecutableName);
        return Directory.Exists(directory) && File.Exists(marker) && File.Exists(executable);
    }

    private static bool HasArgument(string[] args, string value) =>
        args.Any(argument => string.Equals(argument, value, StringComparison.OrdinalIgnoreCase));

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

internal sealed class InstallDirectoryForm : Form
{
    private readonly TextBox _directoryTextBox;

    public InstallDirectoryForm(string defaultDirectory)
    {
        Text = "安装 Emotionball-Deskpet";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = true;
        ClientSize = new Size(540, 190);

        var title = new Label
        {
            AutoSize = true,
            Text = "选择桌宠安装目录",
            Font = new Font(SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont, FontStyle.Bold),
            Location = new Point(20, 18),
        };
        Controls.Add(title);

        var description = new Label
        {
            AutoSize = false,
            Text = "安装程序会把桌宠和卸载程序放入此目录。已有的 Emotionball-Deskpet 安装目录可以直接覆盖。",
            Location = new Point(20, 50),
            Size = new Size(500, 36),
        };
        Controls.Add(description);

        _directoryTextBox = new TextBox
        {
            Location = new Point(20, 98),
            Size = new Size(405, 27),
            Text = defaultDirectory,
        };
        Controls.Add(_directoryTextBox);

        var browseButton = new Button
        {
            Text = "浏览…",
            Location = new Point(435, 96),
            Size = new Size(80, 30),
        };
        browseButton.Click += BrowseButton_Click;
        Controls.Add(browseButton);

        var installButton = new Button
        {
            Text = "安装",
            DialogResult = DialogResult.OK,
            Location = new Point(354, 143),
            Size = new Size(80, 30),
        };
        installButton.Click += InstallButton_Click;
        Controls.Add(installButton);

        var cancelButton = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            Location = new Point(435, 143),
            Size = new Size(80, 30),
        };
        Controls.Add(cancelButton);

        AcceptButton = installButton;
        CancelButton = cancelButton;
    }

    public string InstallDirectory
    {
        get
        {
            var value = _directoryTextBox.Text.Trim();
            return string.IsNullOrWhiteSpace(value) ? string.Empty : Path.GetFullPath(value);
        }
    }

    private void BrowseButton_Click(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择 Emotionball-Deskpet 的安装目录",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(_directoryTextBox.Text)
                ? _directoryTextBox.Text
                : Path.GetDirectoryName(_directoryTextBox.Text) ?? string.Empty,
            ShowNewFolderButton = true,
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _directoryTextBox.Text = dialog.SelectedPath;
        }
    }

    private void InstallButton_Click(object? sender, EventArgs e)
    {
        try
        {
            var directory = InstallDirectory;
            var root = Path.GetPathRoot(directory);
            if (string.IsNullOrWhiteSpace(directory) || string.Equals(directory, root, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("请选择一个具体的安装目录，不要直接选择磁盘根目录。");
            }

            if (Directory.Exists(directory) &&
                !File.Exists(Path.Combine(directory, ".payload.sha256")) &&
                Directory.EnumerateFileSystemEntries(directory).Any())
            {
                throw new IOException("目标目录已存在且不为空，请选择空目录或之前的 Emotionball-Deskpet 安装目录。");
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "安装目录无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            DialogResult = DialogResult.None;
        }
    }
}
