using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Zarp.Core
{
    public enum EngineState { Idle, Preparing, Searching, Connecting, Connected, Disconnecting, Unknown }

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
        /// <summary>
        /// Найден сторонний VPN. Получает его адаптеры и признак «идёт поиск стратегии» (а не просто подключение).
        /// Возвращает true, если всё равно продолжать. Без обработчика поиск через чужой VPN не начинается.
        /// </summary>
        public Func<List<string>, bool, bool> AskContinueWithVpn;
        /// <summary>Поиск сторонних VPN. Подменяется в тестах, чтобы результат не зависел от сетей компьютера.</summary>
        internal Func<List<string>> FindForeignVpns = NetCheck.ForeignVpnAdapters;
        /// <summary>Cloudflare WARP не найден. Возвращает true, если пользователь разрешил скачать и установить его.</summary>
        public Func<bool> AskInstallWarp;
        /// <summary>Установка WARP. Подменяется в тестах, чтобы они ничего не скачивали и не ставили.</summary>
        internal Func<IProgress<WarpStep>, CancellationToken, Task> InstallWarp;
        /// <summary>Сколько ждать запуска службы WARP после установки.</summary>
        internal int WarpReadyTimeoutMs = 90000;
        internal Func<int, CancellationToken, Task<int>> MeasureTraffic = Warp.MeasureAsync;

        CancellationTokenSource _cts;
        volatile bool _warpReporting;
        /// <summary>
        /// Текущая операция идёт при включённом стороннем VPN. Трафик WARP тогда уходит в чужой туннель,
        /// поэтому неудача ничего не говорит о стратегии и не должна затирать сохранённые результаты.
        /// </summary>
        volatile bool _foreignVpn;
        readonly SemaphoreSlim _busy = new SemaphoreSlim(1, 1);
        volatile bool _stopping;
        Task _shutdownTask;
        readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        bool _backgroundStarted;
        Strategy _activeStrategy;
        CancellationTokenSource _observation;

        public Engine(string dataDir)
        {
            DataDir = dataDir;
            string cfg = Path.Combine(dataDir, "zarp.json");
            string legacy = Path.Combine(dataDir, "zwarp.json"); // до переименования программа звалась ZWARP
            try { if (!File.Exists(cfg) && File.Exists(legacy)) File.Move(legacy, cfg); } catch { }
            Config = AppConfig.Load(cfg);
            Zapret = new Zapret(Path.Combine(dataDir, "zapret2"));
            InstallWarp = (progress, ct) => WarpInstaller.InstallAsync(DataDir, progress, ct);
            ReloadStrategies();

            // результаты старых наборов стратегий больше не нужны: id сменились, и тесты были без перепроверки
            foreach (var id in Config.Results.Keys.Where(id => Strategies.All(s => s.Id != id)).ToList())
                Config.Results.Remove(id);
            if (Config.SelectedStrategyId == "h3-fake-google") Config.SelectedStrategyId = "warp-q-google6";
        }

        public void ReloadStrategies()
        {
            Strategies = StrategyCatalog.Load(DataDir);
            if (Selected == null && Config.SelectedStrategyId != null)
            {
                var matches = Strategies.Where(s => s.Custom && s.LegacyId == Config.SelectedStrategyId).ToList();
                if (matches.Count == 1) Config.SelectedStrategyId = matches[0].Id;
            }
            foreach (var id in Config.Results.Keys.Where(id => Strategies.All(s => s.Id != id)).ToList())
                Config.Results.Remove(id);
        }

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

        public void Cancel()
        {
            try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        }

        // ------------------------------------------------------------------ сценарии верхнего уровня

        /// <summary>Главная кнопка: если стратегия уже выбрана - подключиться, иначе найти лучшую и подключиться.</summary>
        public Task ConnectAsync() => Run(async ct =>
        {
            if (!await PrepareAsync(ct, searching: Selected == null)) return;
            var s = Selected;
            if (s != null)
            {
                if (await ApplyAsync(s, ct)) return;
                if (_foreignVpn)
                {
                    // через чужой VPN рукопожатие WARP не доходит ни с какой стратегией: ничего не перезаписываем и не ищем заново
                    Log.Write(L.T("log.vpnConnectFailed"));
                    Set(EngineState.Idle, M("detail.vpnOff"));
                    return;
                }
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
            if (!await PrepareAsync(ct, searching: true)) return;
            if (full) await SearchAndApplyAsync(Strategies, 0, "log.searchFull", ct);
            else await SearchAndApplyAsync(Strategies, QuickStopAfter, "log.searchQuick", ct);
        });

        /// <summary>Проверить только выбранные в настройках стратегии (все, без остановки).</summary>
        public Task TestStrategiesAsync(IEnumerable<Strategy> only) => Run(async ct =>
        {
            if (!await PrepareAsync(ct, searching: true)) return;
            await SearchAndApplyAsync(only.ToList(), 0, "log.searchSelected", ct);
        });

        /// <summary>Применить конкретную стратегию (из настроек) и запомнить её.</summary>
        public Task UseStrategyAsync(Strategy s) => Run(async ct =>
        {
            if (!await PrepareAsync(ct, searching: false)) return;
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
            if (_stopping || !await _busy.WaitAsync(0)) return;
            try
            {
                if (_stopping) return;
                Set(EngineState.Preparing, M("detail.preparing"));
                // распаковать вшитый zapret2 заранее; ошибки (антивирус) разберёт PrepareAsync
                try { Zapret.ExtractEmbedded(); }
                catch (Exception e) { Log.Write(L.T("log.notExtracted", e.Message)); }
                if (!Warp.Installed)
                {
                    Log.Write(L.T("log.warpMissing", string.Join("; ", Warp.LastSearch?.Report ?? new List<string>())));
                    Set(EngineState.Idle, M("detail.noWarp"));
                    return;
                }
                Log.Write(L.T("log.warpFound", Warp.CliPath));
                await ObserveConnectionAsync(_lifetime.Token);
            }
            catch (OperationCanceledException) when (_stopping) { }
            catch (Exception e) { Log.Write(L.T("log.error", e.Message)); Set(EngineState.Unknown, M("detail.error", e.Message)); }
            finally { _busy.Release(); }
        }

        public async Task MonitorAsync()
        {
            if (_stopping || !Warp.Installed || !await _busy.WaitAsync(0)) return;
            var observation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _observation = observation;
            try
            {
                if (!_stopping) await ObserveConnectionAsync(observation.Token);
            }
            catch (OperationCanceledException) when (observation.IsCancellationRequested) { }
            catch (Exception e) { Log.Write(L.T("log.error", e.Message)); Set(EngineState.Unknown, M("detail.error", e.Message)); }
            finally { _observation = null; observation.Dispose(); _busy.Release(); }
        }

        async Task ObserveConnectionAsync(CancellationToken ct)
        {
            var (status, reason) = await Warp.StatusAsync(ct);
            ct.ThrowIfCancellationRequested();
            if (status == "Disconnected")
            {
                if (_activeStrategy != null) Zapret.Stop();
                _activeStrategy = null;
                // фоновая проверка не должна каждые 15 секунд стирать итог последней операции («выключите VPN», «не найдено»)
                if (State != EngineState.Idle)
                    Set(EngineState.Idle, Selected == null ? M("detail.noStrategy") : M("detail.disconnected"));
                return;
            }
            if (status != "Connected")
            {
                Set(EngineState.Unknown, M("detail.connectionUnknown", reason ?? ""));
                return;
            }
            if (_activeStrategy?.UsesZapret == true && !Zapret.Running)
            {
                Set(EngineState.Unknown, M("detail.winwsFailed"));
                return;
            }
            if (await MeasureTraffic(1, ct) < 0)
            {
                Set(EngineState.Unknown, M("err.noTraffic"));
                return;
            }
            ct.ThrowIfCancellationRequested();
            if (_activeStrategy == null && Selected?.UsesZapret == true && Zapret.Running)
                _activeStrategy = Selected;
            Set(EngineState.Connected, DescribeSelected());
        }

        // ------------------------------------------------------------------ фоновое обновление zapret2

        /// <summary>Запустить фоновую проверку обновлений zapret2: через минуту после старта и далее каждые 12 часов.</summary>
        public void StartBackgroundUpdates()
        {
            if (_backgroundStarted || _stopping) return;
            _backgroundStarted = true;
            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), _lifetime.Token);
                    while (!_stopping)
                    {
                        if (Config.AutoUpdateZapret) await CheckZapretUpdateAsync(false);
                        await Task.Delay(TimeSpan.FromHours(12), _lifetime.Token);
                    }
                }
                catch (OperationCanceledException) when (_stopping) { }
            });
        }

        /// <summary>Проверить и, если есть, скачать и применить новую версию zapret2.</summary>
        public async Task CheckZapretUpdateAsync(bool verbose)
        {
            if (_stopping) return;
            string tag;
            try
            {
                tag = await Zapret.DownloadUpdateAsync(_lifetime.Token);
            }
            catch (OperationCanceledException) when (_stopping) { return; }
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
                    var err = await Zapret.StartAsync(s, Config.RestrictToWarpIps, _lifetime.Token);
                    if (err != null)
                    {
                        Log.Write(L.T("log.updateRestartFailed", err));
                        Set(EngineState.Unknown, M("detail.winwsFailed"));
                    }
                }
                else if (!Zapret.Running)
                {
                    Zapret.ApplyPendingUpdate();
                }
            }
            catch (OperationCanceledException) when (_stopping) { }
            catch (Exception e) { Log.Write(L.T("log.updateCheckFailed", e.Message)); }
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
            _lifetime.Cancel();
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

        /// <summary>Windows не должен ждать обычного цикла закрытия окна.</summary>
        public void ShutdownForSessionEnd()
        {
            _stopping = true;
            _lifetime.Cancel();
            Cancel();
            if (!Config.DisconnectOnExit) return;
            // Продолжение не зависит от UI. У ОС остаётся право завершить процесс по своему таймауту.
            var cleanup = Task.Run(async () =>
            {
                using (var limit = new CancellationTokenSource(1500))
                {
                    try { if (Warp.Installed) await Warp.Cli("disconnect", 1500, limit.Token).ConfigureAwait(false); }
                    catch (Exception e) { Log.Write(L.T("log.error", e.Message)); }
                    finally { Zapret.Stop(); }
                }
            });
            try { cleanup.Wait(1800); } catch { }
        }

        async Task Run(Func<CancellationToken, Task> body)
        {
            if (_stopping) return;
            if (!await _busy.WaitAsync(0))
            {
                var observation = _observation;
                if (observation == null) return; // другая пользовательская операция
                try { observation.Cancel(); } catch (ObjectDisposedException) { }
                // Нажатие кнопки имеет приоритет над фоновым измерением.
                await _busy.WaitAsync();
            }
            if (_stopping) { _busy.Release(); return; }
            _cts = new CancellationTokenSource();
            _foreignVpn = false;
            try
            {
                await body(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                Log.Write(L.T("log.cancelled"));
                bool stopped = await TryStopAllAsync();
                Set(stopped ? EngineState.Idle : EngineState.Unknown, M("detail.cancelled"));
            }
            catch (Exception e)
            {
                Log.Write(L.T("log.error", e.Message));
                bool stopped = await TryStopAllAsync();
                Set(stopped ? EngineState.Idle : EngineState.Unknown, M("detail.error", e.Message));
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

        /// <summary>
        /// Убедиться, что Cloudflare WARP есть. Если его нет, предложить скачать и установить официальный клиент.
        /// Возвращает true, когда warp-cli найден и служба WARP отвечает.
        /// </summary>
        internal async Task<bool> EnsureWarpAsync(CancellationToken ct)
        {
            if (Warp.Installed) return true;
            var search = Warp.Locate(); // WARP могли поставить уже после запуска Zarp
            if (Warp.Installed) return true;

            Log.Write(L.T("log.warpMissing", string.Join("; ", search.Report)));
            if (AskInstallWarp == null || !AskInstallWarp())
            {
                Log.Write(L.T("log.noWarpCli"));
                Set(EngineState.Idle, M("detail.noWarp"));
                return false;
            }

            try
            {
                Set(EngineState.Preparing, M("progress.warpDownloading", 0));
                // Progress<T> доставляет отчёты с задержкой: запоздавший отчёт не должен вернуть полосу загрузки
                // и старый текст после того, как установка уже закончилась.
                _warpReporting = true;
                var progress = new Progress<WarpStep>(step =>
                {
                    if (!_warpReporting) return;
                    ProgressTotal = step.Percent >= 0 ? 100 : 0;
                    ProgressDone = Math.Max(0, step.Percent);
                    SetDetail(step.Message);
                });
                try { await InstallWarp(progress, ct); }
                finally { _warpReporting = false; }

                var after = Warp.Locate(); // после установки warp-cli должен найтись
                if (!Warp.Installed)
                    throw new Exception(L.T("log.warpMissing", string.Join("; ", after.Report)));
                Log.Write(L.T("log.warpInstalled", Warp.CliPath));

                ProgressTotal = 0;
                Set(EngineState.Preparing, M("progress.warpStarting"));
                if (!await Warp.WaitReadyAsync(WarpReadyTimeoutMs, ct))
                    throw new Exception(L.T("err.warpNotReady"));
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                ProgressTotal = 0;
                Log.Write(L.T("log.warpInstallFailed", e.Message));
                Log.Write(L.T("log.warpManual", DataDir));
                Set(EngineState.Idle, M("detail.warpInstallFailed"));
                return false;
            }
        }

        /// <param name="searching">Дальше будет подбор стратегии (а не подключение с уже выбранной).</param>
        async Task<bool> PrepareAsync(CancellationToken ct, bool searching)
        {
            Set(EngineState.Preparing, M("detail.preparing"));
            if (!await EnsureWarpAsync(ct)) return false;
            if (!Zapret.Installed || Zapret.EmbeddedIsNewer)
            {
                if (!await InstallZapretAsync(ct) && !Zapret.Installed) return false;
            }
            foreach (var other in Zapret.ForeignDpiTools())
                Log.Write(L.T("log.otherDpi", other));
            var vpns = FindForeignVpns();
            _foreignVpn = vpns.Count > 0;
            if (_foreignVpn)
            {
                // трафик WARP уйдёт в чужой туннель, и zapret на него не повлияет: каждая проверка измерит этот VPN, а не сеть
                foreach (var v in vpns)
                    Log.Write(L.T("log.otherVpn", v));
                Log.Write(L.T(searching ? "log.vpnNoSearch" : "log.vpnAdvice"));
                // без окна (автозапуск, тесты) подключиться с выбранной стратегией можно, а подбирать новую через чужой VPN нельзя
                bool go = AskContinueWithVpn?.Invoke(vpns, searching) ?? !searching;
                if (!go)
                {
                    _foreignVpn = false;
                    Set(EngineState.Idle, M("detail.vpnOff"));
                    return false;
                }
            }
            if (!await Warp.EnsureRegisteredAsync(ct))
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
        /// При изоляции выбирается наименее использованный эндпоинт, отличный от первого теста этой пары.
        /// </summary>
        async Task<TestResult> TestAsync(Strategy s, CancellationToken ct, bool isolate, string previousEndpoint = null)
        {
            var res = new TestResult { StrategyId = s.Id, When = DateTime.Now };
            await Warp.DisconnectAsync(ct);
            await Warp.SetTransportAsync(s.Transport, ct);
            if (isolate)
            {
                res.Endpoint = Warp.NextEndpoint(s.Transport, previousEndpoint);
                res.EndpointReused = Warp.EndpointWasReused;
                if (!await Warp.SetEndpointAsync(res.Endpoint, ct)) { res.Fail(M("err.endpoint")); return res; }
            }
            var err = await Zapret.StartAsync(s, Config.RestrictToWarpIps, ct);
            if (err != null) { res.Fail(err); return res; }

            await Warp.ConnectAsync(ct);
            int connectMs = await Warp.WaitConnectedAsync(Math.Max(1, Math.Min(60, Config.TestTimeoutSec)) * 1000, ct);
            if (connectMs < 0) { res.Fail(M("err.timeout", Config.TestTimeoutSec)); return res; }

            int ping = await MeasureTraffic(3, ct);
            if (ping < 0) { res.Fail(M("err.noTraffic")); return res; }

            res.Ok = true;
            res.ConnectMs = connectMs;
            res.PingMs = ping;
            return res;
        }

        /// <param name="stopAfter">Остановить перебор после стольких рабочих стратегий; 0 - проверить все.</param>
        async Task SearchAndApplyAsync(List<Strategy> list, int stopAfter, string logKey, CancellationToken ct)
        {
            bool isolate = Config.IsolateTests;
            int candidates = 0, confirmedNow = 0;

            Log.Write(L.T(logKey, list.Count, stopAfter));
            ProgressTotal = list.Count;
            ProgressDone = 0;

            try
            {
                // Перепроверяем до остановки быстрого поиска: неподтверждённый кандидат не занимает квоту.
                foreach (var s in list)
                {
                    ct.ThrowIfCancellationRequested();
                    Set(EngineState.Searching, M("detail.testing", ProgressDone + 1, list.Count, s));
                    var r = await TestAsync(s, ct, isolate);
                    ProgressDone++;
                    Log.Write("  " + (r.Ok
                        ? L.T("log.testOk", s.Name, r.ConnectMs, r.PingMs)
                        : L.T("log.testFail", s.Name, r.DisplayError)));
                    if (r.Ok)
                    {
                        candidates++;
                        Log.Write(L.T("log.recheck", 1));
                        ct.ThrowIfCancellationRequested();
                        Set(EngineState.Searching, M("detail.rechecking", ProgressDone, list.Count, s));
                        var r2 = await TestAsync(s, ct, isolate, r.Endpoint);
                        if (r2.Ok)
                        {
                            confirmedNow++;
                            Config.Results[s.Id] = new TestResult
                            {
                                StrategyId = s.Id, When = r2.When, Ok = true, Confirmed = true,
                                ConnectMs = Math.Max(r.ConnectMs, r2.ConnectMs),
                                PingMs = (int)(((long)r.PingMs + r2.PingMs) / 2),
                                Endpoint = r2.Endpoint,
                                EndpointReused = r.EndpointReused || r2.EndpointReused,
                                Independent = isolate && !_foreignVpn && !r.EndpointReused && !r2.EndpointReused && r.Endpoint != r2.Endpoint,
                            };
                            Log.Write("  " + L.T("log.recheckOk", s.Name, r2.ConnectMs, r2.PingMs));
                        }
                        else
                        {
                            r2.Rechecked = true;
                            if (!_foreignVpn) Config.Results[s.Id] = r2;
                            Log.Write("  " + L.T("log.testFail", s.Name, r2.DisplayError));
                        }
                    }
                    else if (!_foreignVpn) Config.Results[s.Id] = r;
                    Config.Save();
                    if (stopAfter > 0 && confirmedNow >= stopAfter) break;
                }
            }
            finally
            {
                // вернуть WARP автоматический выбор эндпоинта
                if (isolate && !await Warp.SetEndpointAsync(null)) throw new IOException(L.T("err.endpoint"));
            }

            if (_foreignVpn && confirmedNow == 0)
            {
                // «ни одна не сработала» при чужом VPN ожидаемо, а не повод искать ошибку в стратегиях.
                // Прежние результаты остаются как были и не применяются: новых данных они не получили.
                await StopAllAsync();
                Log.Write(L.T("log.vpnConnectFailed"));
                Set(EngineState.Idle, M("detail.vpnOff"));
                return;
            }

            var confirmed = ConfirmedStrategies(null);
            if (confirmed.Count == 0)
            {
                await StopAllAsync();
                Log.Write(candidates > 0
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
            if (_foreignVpn) return; // через чужой VPN неудача ничего не говорит о самой стратегии
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

            await Warp.DisconnectAsync(ct);
            await Warp.SetTransportAsync(s.Transport, ct);
            var err = await Zapret.StartAsync(s, Config.RestrictToWarpIps, ct);
            if (err != null)
            {
                Log.Write(err.ToString());
                Set(EngineState.Idle, M("detail.winwsFailed"));
                return false;
            }
            await Warp.ConnectAsync(ct);
            int ms = await Warp.WaitConnectedAsync(Math.Max(30, Math.Min(60, Config.TestTimeoutSec) * 2) * 1000, ct);
            if (ms < 0 || await MeasureTraffic(1, ct) < 0)
            {
                Log.Write(L.T("log.warpNotConnected"));
                await StopAllAsync();
                Set(EngineState.Idle, M("detail.connectFailed"));
                return false;
            }
            ct.ThrowIfCancellationRequested();
            _activeStrategy = s;
            Log.Write(L.T("log.warpConnectedIn", ms));
            Set(EngineState.Connected, M("detail.strategy", s));
            return true;
        }

        async Task StopAllAsync()
        {
            try
            {
                using (var limit = new CancellationTokenSource(10000))
                {
                    if (Warp.Installed)
                    {
                        await Warp.DisconnectAsync(limit.Token);
                        if (!await Warp.SetEndpointAsync(null, limit.Token)) throw new IOException(L.T("err.endpoint"));
                    }
                }
            }
            finally { Zapret.Stop(); _activeStrategy = null; }
        }

        async Task<bool> TryStopAllAsync()
        {
            try { await StopAllAsync(); return true; }
            catch (Exception e) { Log.Write(L.T("log.error", e.Message)); return false; }
        }

        Msg DescribeSelected()
        {
            var s = Selected;
            return s == null ? M("detail.warpConnected") : M("detail.strategy", s);
        }
    }
}
