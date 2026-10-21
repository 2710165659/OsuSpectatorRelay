using System.Diagnostics;
using OsuSpectatorRelay.Networking;

namespace OsuSpectatorRelay.Hosting;

public static class Launcher
{
    public static ProcessStartInfo SelfStartInfo(string command, AppFiles files)
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定程序路径。");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = AppContext.BaseDirectory
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "OsuSpectatorRelay.dll"));
        start.ArgumentList.Add(command);
        start.ArgumentList.Add("--config");
        start.ArgumentList.Add(files.ConfigPath);
        return start;
    }

    public static async Task StartAsync(AppConfig config, AppFiles files)
    {
        if (string.IsNullOrWhiteSpace(config.OsuPath) || !File.Exists(config.OsuPath))
            throw new InvalidOperationException("请先在程序旁的 config.json 填写 osuPath，指向实际的 osu 可执行文件。");
        _ = config.GetWorkerUri("/");

        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(config.OsuPath)))
        {
            using (process)
            {
                bool same = false;
                try { same = string.Equals(process.MainModule?.FileName, config.OsuPath, StringComparison.OrdinalIgnoreCase); }
                catch (System.ComponentModel.Win32Exception) { }
                if (same) throw new InvalidOperationException("osu 已在运行。请先退出，再使用本工具生成的快捷方式启动，以便应用本地代理配置。");
            }
        }

        var status = await ControlPipe.SendAsync(files, new ControlRequest("ping")).ConfigureAwait(false);
        if (status is null)
        {
            using var service = Process.Start(SelfStartInfo("serve", files)) ?? throw new IOException("无法启动后台服务。");
            for (int i = 0; i < 40 && status is null; i++)
            {
                await Task.Delay(200).ConfigureAwait(false);
                status = await ControlPipe.SendAsync(files, new ControlRequest("ping"), 200).ConfigureAwait(false);
                if (service.HasExited && status is null) break;
            }
        }
        if (status is null) throw new IOException("后台代理未能启动。请检查端口占用和 runtime/relay.log。");
        if (status.Port != config.ListenPort) throw new IOException("配置端口已修改，请先执行 stop 再启动。");

        if (config.CheckBeforeLaunch)
            await StartupReadiness.WarmUpAsync(config, status.Port, new RelayLog(files)).ConfigureAwait(false);

        string localProxy = $"http://127.0.0.1:{status.Port}";
        var game = new ProcessStartInfo(config.OsuPath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(config.OsuPath)!
        };
        foreach (string argument in config.OsuArguments) game.ArgumentList.Add(argument);
        // Scope overrides to this child; neither the user's Windows proxy nor parent environment is changed.
        foreach (string name in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY" }) game.Environment[name] = localProxy;
        game.Environment["NO_PROXY"] = "localhost,127.0.0.1,::1";
        using var launched = Process.Start(game) ?? throw new IOException("无法启动 osu。");
        await ControlPipe.SendAsync(files, new ControlRequest("register", launched.Id)).ConfigureAwait(false);
    }

    public static string CreateShortcut(AppConfig config, AppFiles files)
    {
        if (string.IsNullOrWhiteSpace(config.OsuPath) || !File.Exists(config.OsuPath))
            throw new InvalidOperationException("请先在 config.json 正确填写 osuPath。快捷方式将直接使用这个 osu 程序的图标。");
        string executable = Environment.ProcessPath!;
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请使用构建后的 EXE 生成快捷方式。");
        string directory = Path.GetDirectoryName(executable)!;
        string path = Path.Combine(directory, "osu! (Relay).lnk");
        WindowsShortcut.Save(path, executable, "start --config \"" + files.ConfigPath + "\"",
            Path.GetDirectoryName(files.ConfigPath)!, Path.GetFullPath(config.OsuPath));
        return path;
    }
}
