using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Zarp.Core
{
    public enum EngineState { Idle, Preparing, Searching, Connecting, Connected, Disconnecting }

    /// <summary>Связывает WARP и zapret2: поиск стратегии, подключение, отключение.</summary>
    public sealed class Engine
    {
        public string DataDir { get; }
        public AppConfig Config { get; }
        public Warp Warp { get; } = new Warp();
        public Zapret Zapret { get; }
        public List<Strategy> Strategies { get; private set; }

        public EngineState State { get; private set; } = EngineState.Idle;
        Msg _detail;
        /// <summary>Короткий текст для UI: что сейчас происходит. Переводится в момент чтения.</summary>
        public string Detail => _detail?.ToString() ?? "";
        public int ProgressDone { get; private set; }
        public int ProgressTotal { get; private set; }

        /// <summary>Вызывается при любом изменении State/Detail/прогресса (из фонового потока).</summary>
        public event Action Changed;
        /// <summary>Нужно разрешение пользователя добавить исключение антивируса. Возвращает true, если можно.</summary>
        public Func<string, bool> AskAntivirusExclusion;
        /// <summary>Найден сторонний VPN. Возвращает true, если всё равно продолжать.</summary>
        public Func<List<string>, bool> AskContinueWithVpn;

        CancellationTokenSource _cts;
        readonly SemaphoreSlim _busy = new SemaphoreSlim(1, 1);
        volatile bool _stopping;
        Task _shutdownTask;

        public Engine(string dataDir)
        {
            DataDir = dataDir;
            string cfg = Path.Combine(dataDir, "zarp.json");
            string legacy = Path.Combine(dataDir, "zwarp.json"); // до переименования программа звалась ZWARP
            try { if (!File.Exists(cfg) && File.Exists(legacy)) File.Move(legacy, cfg); } catch { }
            Config = AppConfig.Load(cfg);
            Zapret = new Zapret(Path.Combine(dataDir, "zapret2"));
            ReloadStrategies();

            // результаты старых наборов стратегий больше не нужны: id сменились, и тесты были без перепроверки
            foreach (var id in Config.Results.Keys.Where(id => Strategies.All(s => s.Id != id)).ToList())
                Config.Results.Remove(id);
            if (Config.SelectedStrategyId == "h3-fake-google") Config.SelectedStrategyId = "warp-q-google6";
        }

        public void ReloadStrategies() => Strategies = StrategyCatalog.Load(DataDir);

        public Strategy Selected => Strategies.FirstOrDefault(s => s.Id == Config.SelectedStrategyId);

        public bool IsBusy => State == EngineState.Preparing || State == EngineState.Searching
                              || State == EngineState.Connecting || State == EngineState.Disconnecting;

        static Msg M(string key, params object[] args) => new Msg(key, args);

        void Set(EngineState st, Msg detail = null)
        {
            State = st;
            if (detail != null) _detail = detail;
            Changed?.Invoke();
        }

        void SetDetail(Msg detail)
        {
            _detail = detail;
            Changed?.Invoke();
        }

        public void Cancel() => _cts?.Cancel();

        // ------------------------------------------------------------------ сценарии верхнего уровня

        /// <summary>Главная кнопка: если стратегия уже выбрана - подключиться, иначе найти лучшую и подключиться.</summary>
        public Task ConnectAsync() => Run(async ct =>
        {
            if (!await PrepareAsync(ct)) return;
            var s = Selected;
            if (s != null)
            {
                if (await ApplyAsync(s, ct)) return;
                MarkFailed(s);
                var others = ConfirmedStrategies(s);
                if (others.Count > 0)
                {
                    Log.Write(L.T("log.savedFailed", s.Name));
                    if (await ApplyFirstWorkingAsync(others, ct)) return;
                }
                Log.Write(L.T("log.verifiedFailed"));
            }
            await SearchAndApplyAsync(Strategies, QuickStopAfter, "log.searchQuick", ct);
        });

        /// <summary>Быстрый поиск останавливается после стольких рабочих стратегий.</summary>
        public int QuickStopAfter => Math.Max(1, Config.StopAfterWorking);

        /// <summary>
        /// Поиск лучшей стратегии и подключение. Быстрый останавливается после QuickStopAfter рабочих,
        /// полный проверяет все стратегии: дольше, зато ни одна не пропущена.
        /// </summary>
        public Task SearchAsync(bool full) => Run(async ct =>
        {
            if (!await PrepareAsync(ct)) return;
            if (full) await SearchAndApplyAsync(Strategies, 0, "log.searchFull", ct);
            else await SearchAndApplyAsync(Strategies, QuickStopAfter, "log.searchQuick", ct);
        });

        /// <summary>Проверить только выбранные в настройках стратегии (все, без остановки).</summary>
        public Task TestStrategiesAsync(IEnumerable<Strategy> only) => Run(async ct =>
        {
            if (!await PrepareAsync(ct)) return;
            await SearchAndApplyAsync(only.ToList(), 0, "log.searchSelected", ct);
        });

        /// <summary>Применить конкретную стратегию (из настроек) и запомнить её.</summary>
        public Task UseStrategyAsync(Strategy s) => Run(async ct =>
        {
            if (!await PrepareAsync(ct)) return;
            if (await ApplyAsync(s, ct))
            {
                Config.SelectedStrategyId = s.Id;
                Config.Save();
            }
        });

        public Task DisconnectAsync() => Run(async ct =>
        {
            Set(EngineState.Disconnecting, M("detail.disconnecting"));
            await StopAllAsync();
            Set(EngineState.Idle, M("detail.disconnected"));
        });

        /// <summary>Выяснить текущее состояние при запуске программы.</summary>
        public async Task RefreshStateAsync()
        {
            // распаковать вшитый zapret2 заранее, чтобы первое подключение не ждало; ошибки (антивирус) разберёт PrepareAsync
            try { Zapret.ExtractEmbedded(); }
            catch (Exception e) { Log.Write(L.T("log.notExtracted", e.Message)); }
            if (!Warp.Installed) { Set(EngineState.Idle, M("detail.noWarp")); return; }
            var (status, _) = await Warp.StatusAsync();
            if (status == "Connected")
                Set(EngineState.Connected, DescribeSelected());
            else
                Set(EngineState.Idle, Selected != null ? M("detail.strategy", Selected) : M("detail.noStrategy"));
        }

        // ------------------------------------------------------------------ фоновое обновление zapret2

        /// <summary>Запустить фоновую проверку обновлений zapret2: через минуту после старта и далее каждые 12 часов.</summary>
        public void StartBackgroundUpdates()
        {
            Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromMinutes(1)); // не мешаем запуску и автоподключению
                while (!_stopping)
                {
                    if (Config.AutoUpdateZapret) await CheckZapretUpdateAsync(false);
                    await Task.Delay(TimeSpan.FromHours(12));
                }
            });
        }

        /// <summary>Проверить и, если есть, скачать и применить новую версию zapret2.</summary>
        public async Task CheckZapretUpdateAsync(bool verbose)
        {
            if (_stopping) return;
            string tag;
            try
            {
                tag = await Zapret.DownloadUpdateAsync(CancellationToken.None);
            }
            catch (Exception e)
            {
                Log.Write(L.T("log.updateCheckFailed", e.Message));
                return;
            }
            if (tag == null)
            {
                if (verbose) Log.Write(L.T("log.upToDate", Zapret.Version));
                return;
            }
            Log.Write(L.T("log.updateDownloaded", tag));
            if (_stopping) return;

            // применяем сразу, если ничего не делаем; иначе - при следующем запуске winws2
            if (!await _busy.WaitAsync(0))
            {
                Log.Write(L.T("log.updateOnConnect"));
                return;
            }
            try
            {
                if (_stopping) return;
                var s = Selected;
                if (State == EngineState.Connected && s != null && s.UsesZapret && Zapret.Running)
                {
                    // Туннель WARP уже установлен, zapret нужен только для рукопожатия -
                    // поэтому winws2 можно перезапустить на новой версии, не разрывая подключение.
                    var err = await Zapret.StartAsync(s, Config.RestrictToWarpIps);
                    if (err != null) Log.Write(L.T("log.updateRestartFailed", err));
                }
                else if (!Zapret.Running)
                {
                    Zapret.ApplyPendingUpdate();
                }
            }
            finally
            {
                _busy.Release();
                Changed?.Invoke();
            }
        }

        /// <summary>Уступить место другой копии: всегда отключить WARP и остановить winws2, независимо от настроек.</summary>
        public Task StopForHandoverAsync() => StopAsync(disconnect: true);

        /// <summary>Остановить всё при выходе из программы.</summary>
        public Task ShutdownAsync() => StopAsync(Config.DisconnectOnExit);

        Task StopAsync(bool disconnect)
        {
            if (_shutdownTask != null) return _shutdownTask;
            _stopping = true;
            Cancel();
            return _shutdownTask = FinishShutdownAsync(disconnect);
        }

        async Task FinishShutdownAsync(bool disconnect)
        {
            // StartAsync/warp-cli не всегда завершаются сразу после Cancel: дождаться
            // операции и её очистки, чтобы она не запустила winws2 уже после выхода.
            await _busy.WaitAsync();
            try
            {
                if (disconnect) await StopAllAsync();
            }
            finally { _busy.Release(); }
        }

        async Task Run(Func<CancellationToken, Task> body)
        {
            if (_stopping || !await _busy.WaitAsync(0)) return; // выходим или уже чем-то заняты
            if (_stopping) { _busy.Release(); return; }
            _cts = new CancellationTokenSource();
            try
            {
                await body(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                Log.Write(L.T("log.cancelled"));
                await StopAllAsync();
                Set(EngineState.Idle, M("detail.cancelled"));
            }
            catch (Exception e)
            {
                Log.Write(L.T("log.error", e.Message));
                Set(EngineState.Idle, M("detail.error", e.Message));
            }
            finally
            {
                ProgressTotal = 0;
                _cts.Dispose();
                _cts = null;
                _busy.Release();
                Changed?.Invoke();
            }
        }

        // ------------------------------------------------------------------ шаги

        async Task<bool> PrepareAsync(CancellationToken ct)
        {
            Set(EngineState.Preparing, M("detail.preparing"));
            if (!Warp.Installed)
            {
                Log.Write(L.T("log.noWarpCli"));
                Set(EngineState.Idle, M("detail.noWarp"));
                return false;
            }
            if (!Zapret.Installed || Zapret.EmbeddedIsNewer)
            {
                if (!await InstallZapretAsync(ct) && !Zapret.Installed) return false;
            }
            foreach (var other in Zapret.ForeignDpiTools())
                Log.Write(L.T("log.otherDpi", other));
            var vpns = NetCheck.ForeignVpnAdapters();
            if (vpns.Count > 0)
            {
                // трафик WARP уйдёт в чужой туннель, и zapret на него не повлияет - результаты поиска будут недостоверны
                foreach (var v in vpns)
                    Log.Write(L.T("log.otherVpn", v));
                Log.Write(L.T("log.vpnAdvice"));
                if (AskContinueWithVpn != null && !AskContinueWithVpn(vpns))
                {
                    Set(EngineState.Idle, M("detail.vpnOff"));
                    return false;
                }
            }
            if (!await Warp.EnsureRegisteredAsync())
            {
                Set(EngineState.Idle, M("detail.registerFailed"));
                return false;
            }
            return true;
        }

        async Task<bool> InstallZapretAsync(CancellationToken ct)
        {
            var progress = new Progress<Msg>(m => { Log.Write(m.ToString()); SetDetail(m); });
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    // сначала - zapret2, вшитый в exe; скачивание с GitHub - только если exe собран без него
                    Zapret.ExtractEmbedded();
                    if (!Zapret.Installed)
                        await Zapret.InstallAsync(progress, ct);
                    return true;
                }
                catch (AntivirusBlockedException e) when (attempt == 0)
                {
                    Log.Write(e.Message);
                    bool allow = AskAntivirusExclusion?.Invoke(Zapret.Dir) ?? false;
                    if (!allow || !await Zapret.AddDefenderExclusionAsync())
                    {
                        Set(EngineState.Idle, M("detail.avBlocked"));
                        return false;
                    }
                }
                catch (Exception e) when (!(e is OperationCanceledException))
                {
                    Log.Write(L.T("log.downloadFailed", e.Message));
                    Log.Write(L.T("log.manualInstall", @"winws2.exe, cygwin1.dll, WinDivert.dll, WinDivert64.sys, lua\, files\fake\", Zapret.Dir));
                    Set(EngineState.Idle, M("detail.downloadFailed"));
                    return false;
                }
            }
            Set(EngineState.Idle, M("detail.avBlocked"));
            return false;
        }

        /// <summary>
        /// Проверить одну стратегию: поднять winws2, подключить WARP, замерить.
        /// Каждый тест идёт на свой эндпоинт WARP, чтобы не унаследовать состояние DPI от прошлого подключения.
        /// </summary>
        async Task<TestResult> TestAsync(Strategy s, CancellationToken ct)
        {
            var res = new TestResult { StrategyId = s.Id, When = DateTime.Now };
            await Warp.DisconnectAsync();
            await Warp.SetTransportAsync(s.Transport, ct);
            if (Config.IsolateTests)
                await Warp.SetEndpointAsync(Warp.NextEndpoint(s.Transport));
            var err = await Zapret.StartAsync(s, Config.RestrictToWarpIps);
            if (err != null) { res.Fail(err); return res; }

            await Warp.ConnectAsync();
            int connectMs = await Warp.WaitConnectedAsync(Config.TestTimeoutSec * 1000, ct);
            if (connectMs < 0) { res.Fail(M("err.timeout", Config.TestTimeoutSec)); return res; }

            int ping = await Warp.MeasureAsync(3, ct);
            if (ping < 0) { res.Fail(M("err.noTraffic")); return res; }

            res.Ok = true;
            res.ConnectMs = connectMs;
            res.PingMs = ping;
            return res;
        }

        /// <param name="stopAfter">Остановить перебор после стольких рабочих стратегий; 0 - проверить все.</param>
        async Task SearchAndApplyAsync(List<Strategy> list, int stopAfter, string logKey, CancellationToken ct)
        {
            var candidates = new List<(Strategy S, TestResult R)>();

            Log.Write(L.T(logKey, list.Count, stopAfter));
            ProgressTotal = list.Count;
            ProgressDone = 0;

            try
            {
                // ---- фаза 1: быстрый перебор
                foreach (var s in list)
                {
                    ct.ThrowIfCancellationRequested();
                    Set(EngineState.Searching, M("detail.testing", ProgressDone + 1, list.Count, s));
                    var r = await TestAsync(s, ct);
                    Config.Results[s.Id] = r;
                    ProgressDone++;
                    Log.Write("  " + (r.Ok
                        ? L.T("log.testOk", s.Name, r.ConnectMs, r.PingMs)
                        : L.T("log.testFail", s.Name, r.DisplayError)));
                    if (r.Ok)
                    {
                        candidates.Add((s, r));
                        if (stopAfter > 0 && candidates.Count >= stopAfter) break;
                    }
                }
                Config.Save();

                // ---- фаза 2: независимая перепроверка каждого кандидата (другой эндпоинт, заново winws2 и WARP).
                // Отсеивает стратегии, которые «прошли» только благодаря предыдущему удачному подключению.
                if (candidates.Count > 0)
                {
                    Log.Write(L.T("log.recheck", candidates.Count));
                    ProgressDone = 0;
                    ProgressTotal = candidates.Count;
                    int k = 0;
                    foreach (var (s, r1) in candidates.OrderBy(c => c.R.Score).ToList())
                    {
                        ct.ThrowIfCancellationRequested();
                        Set(EngineState.Searching, M("detail.rechecking", ++k, candidates.Count, s));
                        var r2 = await TestAsync(s, ct);
                        ProgressDone++;
                        if (r2.Ok)
                        {
                            Config.Results[s.Id] = new TestResult
                            {
                                StrategyId = s.Id, When = r2.When, Ok = true, Confirmed = true,
                                ConnectMs = Math.Max(r1.ConnectMs, r2.ConnectMs),
                                PingMs = (r1.PingMs + r2.PingMs) / 2,
                            };
                            Log.Write("  " + L.T("log.recheckOk", s.Name, r2.ConnectMs, r2.PingMs));
                        }
                        else
                        {
                            r2.Rechecked = true;
                            Config.Results[s.Id] = r2;
                            Log.Write("  " + L.T("log.testFail", s.Name, r2.DisplayError));
                        }
                        Config.Save();
                    }
                }
            }
            finally
            {
                // вернуть WARP автоматический выбор эндпоинта
                if (Config.IsolateTests) await Warp.SetEndpointAsync(null);
            }

            var confirmed = ConfirmedStrategies(null);
            if (confirmed.Count == 0)
            {
                await StopAllAsync();
                Log.Write(candidates.Count > 0
                    ? L.T("log.candidatesFailed")
                    : L.T("log.noneWorked", StrategyCatalog.CustomFileName));
                Set(EngineState.Idle, M("detail.notFound"));
                return;
            }

            ProgressTotal = 0;
            var best = confirmed[0];
            var br = Config.Results[best.Id];
            Log.Write(L.T("log.best", best.Name, br.ConnectMs, br.PingMs));
            if (!await ApplyFirstWorkingAsync(confirmed, ct))
                Set(EngineState.Idle, M("detail.foundButFailed"));
        }

        /// <summary>Подтверждённые стратегии, лучшие первыми.</summary>
        List<Strategy> ConfirmedStrategies(Strategy except) =>
            Strategies
                .Where(s => s != except && Config.Results.TryGetValue(s.Id, out var r) && r.Ok && r.Confirmed)
                .OrderBy(s => Config.Results[s.Id].Score)
                .ToList();

        /// <summary>Подключиться с первой стратегией из списка, которая реально подключится; её и запомнить.</summary>
        async Task<bool> ApplyFirstWorkingAsync(IEnumerable<Strategy> ordered, CancellationToken ct)
        {
            foreach (var s in ordered)
            {
                ct.ThrowIfCancellationRequested();
                if (await ApplyAsync(s, ct))
                {
                    Config.SelectedStrategyId = s.Id;
                    Config.Save();
                    return true;
                }
                MarkFailed(s);
                Log.Write(L.T("log.tryNext", s.Name));
            }
            return false;
        }

        void MarkFailed(Strategy s)
        {
            var failed = new TestResult { StrategyId = s.Id, When = DateTime.Now, Ok = false };
            failed.Fail(M("result.applyFailed"));
            Config.Results[s.Id] = failed;
            Config.Save();
        }

        /// <summary>Подключиться с заданной стратегией.</summary>
        async Task<bool> ApplyAsync(Strategy s, CancellationToken ct)
        {
            Set(EngineState.Connecting, M("detail.connectingTo", s));
            Log.Write(L.T("log.connectingWith", s.Name));

            await Warp.DisconnectAsync();
            await Warp.SetTransportAsync(s.Transport, ct);
            var err = await Zapret.StartAsync(s, Config.RestrictToWarpIps);
            if (err != null)
            {
                Log.Write(err.ToString());
                Set(EngineState.Idle, M("detail.winwsFailed"));
                return false;
            }
            await Warp.ConnectAsync();
            int ms = await Warp.WaitConnectedAsync(Math.Max(30, Config.TestTimeoutSec * 2) * 1000, ct);
            if (ms < 0)
            {
                Log.Write(L.T("log.warpNotConnected"));
                await StopAllAsync();
                Set(EngineState.Idle, M("detail.connectFailed"));
                return false;
            }
            Log.Write(L.T("log.warpConnectedIn", ms));
            Set(EngineState.Connected, M("detail.strategy", s));
            return true;
        }

        async Task StopAllAsync()
        {
            if (Warp.Installed) await Warp.DisconnectAsync();
            Zapret.Stop();
        }

        Msg DescribeSelected()
        {
            var s = Selected;
            return s == null ? M("detail.warpConnected") : M("detail.strategy", s);
        }
    }
}
