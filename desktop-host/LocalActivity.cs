using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace EmotionBallDesktop;

internal sealed record LocalPhrase(string Text, string State, string EmotionId);

internal sealed record ForegroundActivity(
    string Category,
    string ContextKey,
    string AppName,
    bool IsResponding);

internal sealed class LocalActivityMonitor
{
    private const int MaxWindowTextLength = 512;

    public bool IsInputIdle(TimeSpan threshold)
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) return false;
        var elapsed = unchecked((uint)Environment.TickCount - info.Time);
        return elapsed >= threshold.TotalMilliseconds;
    }

    public ForegroundActivity CaptureForeground()
    {
        var window = GetForegroundWindow();
        if (window == nint.Zero) return DesktopActivity("no-window");

        var className = ReadClassName(window);
        if (className is "Progman" or "WorkerW" or "Shell_TrayWnd")
        {
            return DesktopActivity(className);
        }

        GetWindowThreadProcessId(window, out var processId);
        if (processId == 0 || processId == Environment.ProcessId)
        {
            return DesktopActivity("pet-window");
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            var processName = process.ProcessName.ToLowerInvariant();
            var windowTitle = ReadWindowText(window);
            var category = Classify(processName, windowTitle, className);
            var appName = FriendlyAppName(process, processName, windowTitle, category);
            var responding = true;
            try { responding = process.Responding; } catch { }
            return new ForegroundActivity(
                responding ? category : "unresponsive",
                $"{category}:{processName}:{window.ToInt64()}",
                appName,
                responding);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new ForegroundActivity("generic", $"unknown:{window.ToInt64()}", "这个窗口", true);
        }
    }

    private static ForegroundActivity DesktopActivity(string key) =>
        new("desktop", $"desktop:{key}", "桌面", true);

    private static string Classify(string processName, string title, string className)
    {
        var normalizedTitle = title.ToLowerInvariant();
        if (processName is "code" or "codium" or "cursor" || normalizedTitle.Contains("visual studio code")) return "vscode";
        if (processName is "wechat" or "weixin" or "wechatapp" || normalizedTitle.Contains("微信")) return "wechat";
        if (processName is "msedge" or "chrome" or "firefox" or "brave" or "opera") return "browser";
        if (processName is "pwsh" or "powershell" or "cmd" or "windowsterminal" or "securecrt" or "putty") return "terminal";
        if (processName is "winword" or "wps") return "word";
        if (processName is "excel" or "et") return "excel";
        if (processName is "powerpnt" or "wpp") return "powerpoint";
        if (processName is "devenv" or "rider64" or "idea64" or "pycharm64") return "ide";
        if (processName is "explorer" && className == "CabinetWClass") return "explorer";
        if (normalizedTitle.Contains("tia portal") || processName.Contains("siemens")) return "automation";
        return "generic";
    }

    private static string FriendlyAppName(
        Process process,
        string processName,
        string windowTitle,
        string category)
    {
        var known = category switch
        {
            "vscode" => "VS Code",
            "wechat" => "微信",
            "browser" => "浏览器",
            "terminal" => "终端",
            "word" => "文档",
            "excel" => "表格",
            "powerpoint" => "演示文稿",
            "ide" => "开发工具",
            "explorer" => "文件管理器",
            "automation" => "自动化工程软件",
            _ => null
        };
        if (known is not null) return known;

        try
        {
            var executablePath = process.MainModule?.FileName;
            var description = string.IsNullOrWhiteSpace(executablePath)
                ? null
                : FileVersionInfo.GetVersionInfo(executablePath).FileDescription?.Trim();
            if (!string.IsNullOrWhiteSpace(description)) return LimitName(description);
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }

        if (!string.IsNullOrWhiteSpace(windowTitle))
        {
            var separator = windowTitle.IndexOf(" - ", StringComparison.Ordinal);
            var candidate = separator >= 0 ? windowTitle[(separator + 3)..] : windowTitle;
            if (!string.IsNullOrWhiteSpace(candidate)) return LimitName(candidate.Trim());
        }
        return LimitName(processName);
    }

    private static string LimitName(string value) => value.Length <= 20 ? value : value[..19] + "…";

    private static string ReadWindowText(nint window)
    {
        var length = Math.Clamp(GetWindowTextLength(window) + 1, 2, MaxWindowTextLength);
        var text = new StringBuilder(length);
        GetWindowText(window, text, text.Capacity);
        return text.ToString();
    }

    private static string ReadClassName(nint window)
    {
        var name = new StringBuilder(128);
        GetClassName(window, name, name.Capacity);
        return name.ToString();
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint window, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }
}

internal static class LocalActivityCatalog
{
    private static readonly IReadOnlyDictionary<string, LocalPhrase[]> Profiles =
        new Dictionary<string, LocalPhrase[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["vscode"] =
            [
                new("正在维护地球补丁", "代码维护中", "32"),
                new("把 Bug 关进小黑屋", "排查逻辑中", "30"),
                new("正在和分号友好谈判", "专注编写中", "39"),
                new("代码正在长出新叶子", "整理上下文", "37")
            ],
            ["wechat"] =
            [
                new("正在和老板沟通升职加薪", "沟通中", "39"),
                new("消息已读，灵魂正在加载", "接收消息中", "31"),
                new("表情包正在参加正式会议", "等待回复中", "35"),
                new("社交电量缓慢回升", "沟通完成感", "33")
            ],
            ["browser"] =
            [
                new("正在互联网海里认真捞针", "检索资料中", "40"),
                new("标签页正在悄悄繁殖", "页面加载中", "36"),
                new("认真研究，绝对不是摸鱼", "阅读思考中", "30"),
                new("把网页知识装进口袋", "整理记忆中", "37")
            ],
            ["terminal"] =
            [
                new("正在和命令行秘密接头", "终端作业中", "32"),
                new("让黑窗口再吐一点真话", "分析输出中", "30"),
                new("一行命令准备撬动宇宙", "执行命令中", "39"),
                new("光标停住不代表投降", "等待结果中", "35")
            ],
            ["explorer"] =
            [
                new("正在给文件排队站好", "整理文件中", "32"),
                new("文件夹里藏着小宇宙", "读取目录中", "36"),
                new("桌面收拾得闪闪发光", "整理完成感", "33"),
                new("正在接收一份神秘文件", "接收文件中", "31")
            ],
            ["word"] =
            [
                new("正在把想法熨得平平整整", "文档编辑中", "39"),
                new("文字正在排队领工牌", "组织内容中", "37"),
                new("这一段话值得再想一下", "推敲措辞中", "30"),
                new("句号已经安全抵达", "段落完成感", "33")
            ],
            ["excel"] =
            [
                new("单元格正在召开晨会", "表格计算中", "30"),
                new("公式今天也在努力工作", "处理数据中", "32"),
                new("正在从数字里寻找真相", "分析数据中", "40"),
                new("这一列看起来很有前途", "等待录入中", "35")
            ],
            ["powerpoint"] =
            [
                new("正在给灵感穿上正装", "制作演示中", "39"),
                new("下一页马上更加精彩", "构思页面中", "30"),
                new("标题和图片正在握手", "调整版式中", "32"),
                new("这一页已经可以登台", "页面完成感", "33")
            ],
            ["ide"] =
            [
                new("正在给代码做精密手术", "工程开发中", "32"),
                new("调试器已经戴好听诊器", "诊断问题中", "30"),
                new("编译器正在认真阅卷", "等待构建中", "35"),
                new("上下文正在重新归队", "回忆工程中", "37")
            ],
            ["automation"] =
            [
                new("正在给产线写一封小情书", "自动化工程中", "32"),
                new("PLC 正在认真听指挥", "逻辑检查中", "30"),
                new("联锁条件又来考验耐心", "排查条件中", "34"),
                new("梯形图里真的没有梯子", "等待监控中", "35")
            ],
            ["desktop"] =
            [
                new("正在桌面边缘巡逻", "暂未选择窗口", "41"),
                new("桌面今天看起来很精神", "桌面整理完成", "33"),
                new("正在等一个值得打开的窗口", "等待操作中", "35"),
                new("风平浪静，适合发会儿呆", "待机放空", "04")
            ],
            ["unresponsive"] =
            [
                new("这个窗口好像正在憋大招", "暂时未响应", "34"),
                new("先别催，它正在深呼吸", "等待窗口恢复", "35")
            ],
            ["generic"] =
            [
                new("正在和「{app}」并肩作战", "专注使用中", "32"),
                new("这个窗口看起来很重要", "观察窗口中", "30"),
                new("专注模式已悄悄上线", "整理思路中", "37"),
                new("鼠标今天也很努力", "处理操作中", "39")
            ]
        };

    public static IReadOnlyList<LocalPhrase> Resolve(ForegroundActivity activity) =>
        Profiles.TryGetValue(activity.Category, out var phrases) ? phrases : Profiles["generic"];
}
