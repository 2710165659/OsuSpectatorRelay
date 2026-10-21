using System.Runtime.InteropServices;
using System.Text.Json;
using OsuSpectatorRelay.Hosting;
using OsuSpectatorRelay.Networking;

namespace OsuSpectatorRelay;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        string command = args.Contains("--help") ? "help" : (args.Length > 0 && !args[0].StartsWith('-') ? args[0] : "start");
        string? Option(string name)
        {
            int index = Array.IndexOf(args, name);
            if (index < 0) return null;
            if (index + 1 >= args.Length) throw new InvalidOperationException(name + " 缺少参数。");
            return args[index + 1];
        }
        bool interactive = command is not ("start" or "serve");
        if (interactive) AttachParentConsole();
        AppFiles? files = null;
        RelayLog? log = null;
        try
        {
            files = new AppFiles(Option("--config") ?? Path.Combine(AppContext.BaseDirectory, "config.json"));
            log = new RelayLog(files);
            if (command is "help" or "--help")
            {
                Print("OsuSpectatorRelay\n请直接编辑程序旁的 config.json 设置游戏路径、Worker 地址和密钥。\nstart (默认，无窗口启动)\nshortcut（生成到 EXE 同级目录）\nstatus | stop | check\n所有命令支持 --config <文件>。");
                return 0;
            }
            var config = AppConfig.Load(files.ConfigPath);
            switch (command)
            {
                case "start": await Launcher.StartAsync(config, files).ConfigureAwait(false); break;
                case "serve":
                    await new RelayService(config, files, log).RunAsync(CancellationToken.None).ConfigureAwait(false);
                    break;
                case "shortcut": Print("快捷方式已生成：" + Launcher.CreateShortcut(config, files)); break;
                case "status":
                    var status = await ControlPipe.SendAsync(files, new ControlRequest("ping")).ConfigureAwait(false);
                    Print(status is null ? "后台未运行。" : JsonSerializer.Serialize(status, AppConfig.JsonContext.RelayStatus));
                    break;
                case "stop":
                    Print(await ControlPipe.SendAsync(files, new ControlRequest("stop")).ConfigureAwait(false) is null ? "后台未运行。" : "已请求停止后台。");
                    break;
                case "check":
                    var settings = new WindowsSystemProxy().Read();
                    var target = new Uri("https://spectator.osu.ppy.sh/spectator");
                    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                    {
                        var route = await new WindowsSystemProxy().ResolveAsync(settings, target, config.EnableWpad, timeout.Token).ConfigureAwait(false);
                        Print(route.IsSystemRoute ? "系统路线：" + (route.Proxy?.Authority ?? "按绕过规则直连") : "系统代理关闭，匹配域名将走 Worker。");
                        // Explicit diagnostic can probe Worker even with system proxy enabled.
                        bool ok = await new WorkerClient(config, log, cache: new PreferredIpCache(files, log)).ProbeAsync(timeout.Token).ConfigureAwait(false);
                        Print(ok ? "Worker 密钥、HTTPS 和 WebSocket 检查通过，优选 IP 已保存。" : "Worker 检查失败，请查看 runtime/relay.log。");
                        if (!ok) return 1;
                    }
                    break;
                default: throw new InvalidOperationException("未知命令。使用 help 查看帮助。");
            }
            return 0;
        }
        catch (Exception e)
        {
            string message = e is InvalidOperationException or IOException ? e.Message : e.GetType().Name;
            log?.Write("程序错误：" + message);
            if (interactive) Print(message);
            else if (command != "serve") MessageBoxW(0, message + "\n\n配置：" + files?.ConfigPath, "OsuSpectatorRelay", 0x10);
            return 1;
        }
    }

    private static void Print(string message) => Console.WriteLine(message);
    private static void AttachParentConsole()
    {
        if (!AttachConsole(unchecked((uint)-1))) return;
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AttachConsole(uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int MessageBoxW(nint window, string text, string caption, uint type);
}
