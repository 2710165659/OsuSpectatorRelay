using System.Text.Json;
using OsuSpectatorRelay.Networking;

namespace OsuSpectatorRelay.Hosting;

public sealed class RelayService(AppConfig config, AppFiles files, RelayLog log)
{
    public async Task RunAsync(CancellationToken externalToken)
    {
        // Use a named semaphore rather than a thread-affine Mutex in async code.
        using var singleton = new Semaphore(1, 1, "Local\\OsuSpectatorRelay-" + files.InstanceId);
        if (!singleton.WaitOne(0)) return;
        try
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
            using var routes = new RouteMonitor(new WindowsSystemProxy(), log);
            var worker = new WorkerClient(config, log, cache: new PreferredIpCache(files, log));
            var proxy = new LocalProxyServer(config, routes, worker, log);
            var games = new GameLifetime(config.OsuPath);
            RelayStatus Status() => new(Environment.ProcessId, config.ListenPort,
                routes.Capture().Settings.IsEnabled(config.EnableWpad) ? "system-proxy" : "worker-for-matched-hosts",
                worker.SelectedIp, proxy.ActiveConnections, proxy.LastError);

            // Starting the listener before control IPC ensures a successful ping means proxy ready.
            Task server = proxy.RunAsync(stop.Token);
            if (server.IsCompleted) await server.ConfigureAwait(false);
            Task controls = ControlPipe.ListenAsync(files, request =>
            {
                if (request.Command == "register" && request.Pid > 0)
                {
                    games.Register(request.Pid);
                    var state = Status();
                    log.Write($"[游戏启动] 已登记 osu 进程 PID {request.Pid}；路线：{state.Mode}；当前优选 IP：{state.SelectedIp}。多个实时隧道属于游戏正常请求，连接数量将在日志中汇总。");
                    proxy.Touch();
                }
                if (request.Command == "stop") stop.CancelAfter(100);
                return Status();
            }, stop.Token);
            Task monitor = routes.RunAsync(stop.Token);
            Task housekeeping = HousekeepingAsync();
            Task probes = ProbeLoopAsync();
            try
            {
                Task completed = await Task.WhenAny(server, controls, monitor, housekeeping, probes).ConfigureAwait(false);
                await completed.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            finally
            {
                stop.Cancel();
                try { await Task.WhenAll(server, controls, monitor, housekeeping, probes).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                log.Write("后台代理已退出。");
                try { File.Delete(Path.Combine(files.RuntimeDirectory, "status.json")); } catch (IOException) { }
            }

            async Task HousekeepingAsync()
            {
                DateTime? gameGoneAt = null;
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
                while (await timer.WaitForNextTickAsync(stop.Token).ConfigureAwait(false))
                {
                    bool hasGame = games.HasRunningGame();
                    if (hasGame) gameGoneAt = null;
                    else if (games.HasRegisteredGame)
                    {
                        gameGoneAt ??= DateTime.UtcNow;
                        if (DateTime.UtcNow - gameGoneAt.Value >= TimeSpan.FromSeconds(2))
                        {
                            log.Write("osu 游戏进程已退出，关闭残留连接并停止后台。");
                            stop.Cancel(); return;
                        }
                    }
                    if (hasGame || proxy.ActiveConnections > 0) proxy.Touch();
                    proxy.LogSummary();
                    if (DateTime.UtcNow - proxy.LastActivityUtc > TimeSpan.FromSeconds(config.IdleExitSeconds))
                    {
                        log.Write("osu 已退出且代理空闲，自动停止。");
                        stop.Cancel(); return;
                    }
                    string state = Path.Combine(files.RuntimeDirectory, "status.json");
                    try
                    {
                        await File.WriteAllTextAsync(state + ".tmp", JsonSerializer.Serialize(Status(), AppConfig.JsonContext.RelayStatus), stop.Token).ConfigureAwait(false);
                        File.Move(state + ".tmp", state, true);
                    }
                    catch (IOException) { }
                }
            }

            async Task ProbeLoopAsync()
            {
                DateTime lastProbe = DateTime.MinValue;
                bool requested = true;
                ProbeTrigger trigger = ProbeTrigger.Startup;
                var previousGeneration = routes.Capture().Token;
                while (!stop.IsCancellationRequested)
                {
                    var snapshot = routes.Capture();
                    if (snapshot.Token != previousGeneration) { requested = true; trigger = ProbeTrigger.NetworkChange; }
                    previousGeneration = snapshot.Token;
                    if (worker.HasPendingRecovery) { requested = true; trigger = ProbeTrigger.Failure; }
                    // Do not send Worker traffic while an external system proxy is enabled.
                    var elapsed = DateTime.UtcNow - lastProbe;
                    if (!snapshot.Settings.IsEnabled(config.EnableWpad) &&
                        (elapsed >= TimeSpan.FromSeconds(config.ProbeIntervalSeconds) ||
                         (requested && elapsed >= TimeSpan.FromSeconds(10))))
                    {
                        var probeTrigger = requested ? trigger : ProbeTrigger.Periodic;
                        requested = false;
                        using var probeLifetime = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, snapshot.Token);
                        try
                        {
                            if (probeTrigger == ProbeTrigger.Failure) await worker.RecoverPendingAsync(probeLifetime.Token).ConfigureAwait(false);
                            else await worker.MaintainAsync(probeTrigger, probeLifetime.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!stop.IsCancellationRequested) { requested = true; }
                        catch (InvalidOperationException) { log.Write("Worker 地址尚未配置；系统代理路线仍可使用。"); }
                        lastProbe = DateTime.UtcNow;
                    }
                    await Task.Delay(TimeSpan.FromSeconds(2), stop.Token).ConfigureAwait(false);
                }
            }
        }
        finally { singleton.Release(); }
    }
}
