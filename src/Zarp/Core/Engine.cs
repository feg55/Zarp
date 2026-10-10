using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Zarp.Core
{
    public enum EngineState { Idle, Preparing, Searching, Connecting, Connected, Disconnecting, Unknown }

    /// <summary>
    /// Связывает WARP и zapret2: поиск стратегии, подключение, отключение.
    /// Если в настройках включён собственный сервер, вместо WARP подключается он (через sing-box).
    /// </summary>
    public sealed class Engine
    {
        public string DataDir { get; }
        public AppConfig Config { get; }
        public Warp Warp { get; } = new Warp();
        public Zapret Zapret { get; }
        public SingBox SingBox { get; }
        /// <summary>Базы GeoIP и GeoSite для правил маршрутизации.</summary>
        public GeoData Geo { get; }
        /// <summary>Все стратегии: встроенные и свои, либо единственный собственный сервер.</summary>
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
        /// <summary>Работа с sing-box. Подменяется в тестах, чтобы они не запускали процессов и не ходили в сеть.</summary>
        internal ProxyRuntime Proxy;
        /// <summary>
        /// Правила маршрутизации пользователя в виде правил sing-box. Ошибка (нет базы, неизвестная категория)
        /// прерывает подключение: правило не пропускается молча.
        /// </summary>
        internal Func<AppConfig, List<object>> CompileRules;

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
        /// <summary>Работающее подключение через собственный сервер (null, если его нет).</summary>
        ProxySession _proxy;
        /// <summary>Адрес сервера, разрешённый в начале текущей операции (имя разрешается один раз).</summary>
        string _proxyAddress;

        public Engine(string dataDir)
        {
            DataDir = dataDir;
            string cfg = Path.Combine(dataDir, "zarp.json");
            string legacy = Path.Combine(dataDir, "zwarp.json"); // до переименования программа звалась ZWARP
            try { if (!File.Exists(cfg) && File.Exists(legacy)) File.Move(legacy, cfg); } catch { }
            Config = AppConfig.Load(cfg);
            Zapret = new Zapret(Path.Combine(dataDir, "zapret2"));
            SingBox = new SingBox(Path.Combine(dataDir, "singbox"));
            Proxy = new ProxyRuntime(SingBox);
            Geo = new GeoData(Path.Combine(dataDir, "geodata"));
            CompileRules = config => Geo.Compile(config.Routing);
            InstallWarp = (progress, ct) => WarpInstaller.InstallAsync(DataDir, progress, ct);
            ReloadStrategies();

            // результаты старых наборов стратегий больше не нужны: id сменились, и тесты были без перепроверки
            foreach (var id in Config.Results.Keys.Where(id => Strategies.All(s => s.Id != id)).ToList())
                Config.Results.Remove(id);
            if (Config.SelectedStrategyId == "h3-fake-google") Config.SelectedStrategyId = "warp-q-google6";
        }

        /// <summary>Вместо WARP подключается собственный сервер.</summary>
        public bool ProxyMode => Config.UseProxy;

        public void ReloadStrategies()
        {
            Strategies = ProxyMode
                ? (ProxyProfile.TryParse(Config.ProxyUri, out var profile) ? new List<Strategy> { profile.ToStrategy() } : new List<Strategy>())
                : StrategyCatalog.Load(DataDir);
            if (Selected == null && Config.SelectedStrategyId != null)
            {
                var matches = Strategies.Where(s => s.Custom && s.LegacyId == Config.SelectedStrategyId).ToList();
                if (matches.Count == 1) Config.SelectedStrategyId = matches[0].Id;
            }
            foreach (var id in Config.Results.Keys.Where(id => Strategies.All(s => s.Id != id)).ToList())
                Config.Results.Remove(id);
        }

        /// <summary>
        /// Стратегии, подходящие под выбранные страны. Только они видны в списке и участвуют в подборе, быстром и полном
        /// поиске, автоматическом подключении, перепроверках и выборе из сохранённых результатов.
        /// </summary>
        public List<Strategy> Candidates => StrategyFilter.Apply(Strategies, Config.StrategyCountry);

        public Strategy Selected => Candidates.FirstOrDefault(s => s.Id == Config.SelectedStrategyId);

        /// <summary>Свои эндпоинты WARP. Испорченный вручную список считается пустым (проверка в PrepareAsync).</summary>
        List<string> EndpointList() => EndpointParser.TryParse(Config.CustomEndpoints, out var list) ? list : new List<string>();

        /// <summary>
        /// Сохранить эндпоинты WARP и собственный сервер. Если что-то изменилось, прежние проверки и выбранная стратегия
        /// сбрасываются: они относились к другому серверу. Уже работающее подключение не трогается до переподключения.
        /// Возвращает false, пока идёт другая операция.
        /// </summary>
        public bool ApplyConnection(string endpoints, bool useProxy, string proxyUri)
        {
            if (IsBusy) return false;
            endpoints = (endpoints ?? "").Trim();
            proxyUri = (proxyUri ?? "").Trim();
            bool changed = endpoints != Config.CustomEndpoints.Trim() || useProxy != Config.UseProxy || proxyUri != Config.ProxyUri.Trim();
            Config.CustomEndpoints = endpoints;
            Config.UseProxy = useProxy;
            Config.ProxyUri = proxyUri;
            if (changed)
            {
                Config.Results.Clear();
                Config.SelectedStrategyId = null;
            }
            ReloadStrategies();
            Zapret.ExtraAddresses = EndpointParser.Addresses(EndpointList());
            Config.Save();
            Changed?.Invoke();
            return true;
        }

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
                // сервер у нас один: повторный подбор означал бы те же проверки с тем же исходом
                if (s.IsProxy) return;
                var others = ConfirmedStrategies(s);
                if (others.Count > 0)
                {
                    Log.Write(L.T("log.savedFailed", s.Name));
                    if (await ApplyFirstWorkingAsync(others, ct)) return;
                }
                Log.Write(L.T("log.verifiedFailed"));
            }
            await SearchAndApplyAsync(Candidates, QuickStopAfter, "log.searchQuick", ct);
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
            if (full) await SearchAndApplyAsync(Candidates, 0, "log.searchFull", ct);
            else await SearchAndApplyAsync(Candidates, QuickStopAfter, "log.searchQuick", ct);
        });

        /// <summary>Проверить только выбранные в настройках стратегии (все, без остановки). Исключённые фильтром пропускаются.</summary>
        public Task TestStrategiesAsync(IEnumerable<Strategy> only) => Run(async ct =>
        {
            var allowed = new HashSet<string>(Candidates.Select(c => c.Id));
            var list = only.Where(s => allowed.Contains(s.Id)).ToList();
            if (list.Count == 0) return;
            if (!await PrepareAsync(ct, searching: true)) return;
            await SearchAndApplyAsync(list, 0, "log.searchSelected", ct);
        });

        /// <summary>Применить конкретную стратегию (из настроек) и запомнить её.</summary>
        public Task UseStrategyAsync(Strategy s) => Run(async ct =>
        {
            if (Candidates.All(c => c.Id != s.Id)) return; // исключена фильтром стран
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
                if (ProxyMode)
                {
                    // WARP и zapret2 не нужны: смотрим, не работает ли уже подключение через собственный сервер
                    await ObserveConnectionAsync(_lifetime.Token);
                    return;
                }
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
            if (_stopping || (!ProxyMode && !Warp.Installed) || !await _busy.WaitAsync(0)) return;
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

        /// <summary>Состояние подключения через собственный сервер: процесс sing-box жив и сервер отвечает.</summary>
        async Task ObserveProxyAsync(CancellationToken ct)
        {
            if (_proxy == null && ProxyProfile.TryParse(Config.ProxyUri, out var profile))
                _proxy = Proxy.Adopt(profile); // sing-box мог остаться от прошлого запуска Zarp
            if (_proxy == null && Proxy.Running)
            {
                // чужой для этого запуска sing-box (другой сервер, потерянные сведения): он держит адаптер и забирает трафик, а Zarp о нём не знает
                Log.Write(L.T("log.singboxOrphan"));
                Proxy.Stop();
            }
            if (_proxy == null || !Proxy.Running)
            {
                bool lost = _proxy != null && State == EngineState.Connected;
                _proxy = null;
                if (lost) Log.Write(L.T("log.singboxStopped"));
                // фоновая проверка не должна стирать итог последней операции, как и в режиме WARP
                if (State != EngineState.Idle)
                    Set(lost ? EngineState.Unknown : EngineState.Idle,
                        lost ? M("detail.singboxFailed") : Selected == null ? M("detail.noStrategy") : M("detail.disconnected"));
                return;
            }
            var measure = await Proxy.MeasureAsync(_proxy, 1, ProxyTimeoutMs(), ct);
            ct.ThrowIfCancellationRequested();
            if (!measure.Ok)
            {
                Set(EngineState.Unknown, M("err.proxyNoTraffic"));
                return;
            }
            Set(EngineState.Connected, DescribeSelected());
        }

        async Task ObserveConnectionAsync(CancellationToken ct)
        {
            if (ProxyMode)
            {
                await ObserveProxyAsync(ct);
                return;
            }
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
                var s = _activeStrategy ?? Selected;
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
                    finally { Zapret.Stop(); Proxy.Stop(); }
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
            if (ProxyMode) return await PrepareProxyAsync(ct, searching);
            if (!EndpointParser.TryParse(Config.CustomEndpoints, out var endpoints))
            {
                // список правят в окне настроек, где неверный адрес не сохранится, но файл настроек можно испортить вручную
                Log.Write(L.T("log.endpointsInvalid"));
                Set(EngineState.Idle, M("detail.endpointsInvalid"));
                return false;
            }
            Zapret.ExtraAddresses = EndpointParser.Addresses(endpoints);
            if (Proxy.Running)
            {
                // проверки WARP не должны идти через наш собственный туннель
                Log.Write(L.T("log.singboxStopping"));
                Proxy.Stop();
            }
            if (!await EnsureWarpAsync(ct)) return false;
            if (!Zapret.Installed || Zapret.EmbeddedIsNewer)
            {
                if (!await InstallZapretAsync(ct) && !Zapret.Installed) return false;
            }
            foreach (var other in Zapret.ForeignDpiTools())
                Log.Write(L.T("log.otherDpi", other));
            if (!AskAboutForeignVpn(searching)) return false;
            if (!await Warp.EnsureRegisteredAsync(ct))
            {
                Set(EngineState.Idle, M("detail.registerFailed"));
                return false;
            }
            return true;
        }

        /// <summary>
        /// Найти сторонние VPN и, если они есть, спросить, продолжать ли. false - пользователь отказался.
        /// Трафик WARP или собственного сервера уйдёт в чужой туннель, поэтому проверки измерят этот VPN, а не сеть.
        /// </summary>
        bool AskAboutForeignVpn(bool searching)
        {
            var vpns = FindForeignVpns();
            _foreignVpn = vpns.Count > 0;
            if (!_foreignVpn) return true;
            foreach (var v in vpns)
                Log.Write(L.T("log.otherVpn", v));
            Log.Write(L.T(searching ? "log.vpnNoSearch" : "log.vpnAdvice"));
            // без окна (автозапуск, тесты) подключиться с выбранной стратегией можно, а подбирать новую через чужой VPN нельзя
            bool go = AskContinueWithVpn?.Invoke(vpns, searching) ?? !searching;
            if (go) return true;
            _foreignVpn = false;
            Set(EngineState.Idle, M("detail.vpnOff"));
            return false;
        }

        /// <summary>Подготовка к работе через собственный сервер: ссылка, программы, sing-box, сторонние VPN.</summary>
        async Task<bool> PrepareProxyAsync(CancellationToken ct, bool searching)
        {
            if (!ProxyProfile.TryParse(Config.ProxyUri, out _))
            {
                Log.Write(L.T("log.proxyInvalid"));
                Set(EngineState.Idle, M("detail.proxyInvalid"));
                return false;
            }
            if (Config.PerAppProxy && SelectedAppPaths().Count == 0)
            {
                // пустой выбор нельзя молча превращать в «все программы»
                Log.Write(L.T("apps.empty"));
                Set(EngineState.Idle, M("apps.empty"));
                return false;
            }
            _proxyAddress = null;
            // сначала останавливаем свой туннель и WARP: проверки не должны идти ни через один из них
            await StopAllAsync();
            if (!await EnsureSingBoxAsync()) return false;
            return AskAboutForeignVpn(searching);
        }

        /// <summary>Положить sing-box на место (вшитый в exe). Антивирус может его заблокировать: тогда предлагаем исключение.</summary>
        async Task<bool> EnsureSingBoxAsync()
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    if (Proxy.Prepare()) return true;
                    Log.Write(L.T("err.singboxMissing"));
                    Set(EngineState.Idle, M("detail.singboxMissing"));
                    return false;
                }
                catch (AntivirusBlockedException e) when (attempt == 0)
                {
                    Log.Write(e.Message);
                    bool allow = AskAntivirusExclusion?.Invoke(SingBox.Dir) ?? false;
                    if (!allow || !await AddDefenderExclusionAsync(SingBox.Dir))
                    {
                        Set(EngineState.Idle, M("detail.avBlockedSingbox"));
                        return false;
                    }
                }
                catch (Exception e)
                {
                    Log.Write(L.T("log.error", e.Message));
                    Set(EngineState.Idle, M("detail.singboxMissing"));
                    return false;
                }
            }
            Set(EngineState.Idle, M("detail.avBlockedSingbox"));
            return false;
        }

        /// <summary>Добавить папку в исключения Защитника Windows (как для zapret2).</summary>
        static async Task<bool> AddDefenderExclusionAsync(string dir)
        {
            Directory.CreateDirectory(dir);
            string cmd = $"-NoProfile -NonInteractive -Command \"Add-MpPreference -ExclusionPath '{dir.Replace("'", "''")}'\"";
            var r = await ProcessUtil.RunAsync(ProcessUtil.PowerShellExe, cmd, 30000);
            Log.Write(r.Ok ? L.T("log.defenderAdded", dir) : L.T("log.defenderFailed", r.Output));
            return r.Ok;
        }

        static string OwnExecutable()
        {
            try { return System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName; }
            catch { return null; }
        }

        /// <summary>Пути выбранных программ, которые ещё существуют.</summary>
        List<string> SelectedAppPaths() => Config.ProxyApps.Where(File.Exists).ToList();

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
            if (s.IsProxy) return await TestProxyAsync(s, ct);
            var res = new TestResult { StrategyId = s.Id, When = DateTime.Now };
            await Warp.DisconnectAsync(ct);
            await Warp.SetTransportAsync(s.Transport, ct);
            var own = EndpointList();
            if (isolate)
            {
                res.Endpoint = Warp.NextEndpoint(s.Transport, previousEndpoint, own);
                res.EndpointReused = Warp.EndpointWasReused;
                if (!await Warp.SetEndpointAsync(res.Endpoint, ct)) { res.Fail(M("err.endpoint")); return res; }
            }
            else if (own.Count > 0)
            {
                // без изоляции проверки идут на первый из своих адресов
                res.Endpoint = own[0];
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
                if (!ProxyMode && (isolate || EndpointList().Count > 0) && !await Warp.SetEndpointAsync(null))
                    throw new IOException(L.T("err.endpoint"));
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
            Candidates
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
            if (s.IsProxy) return await ApplyProxyAsync(s, ct);

            await Warp.DisconnectAsync(ct);
            await Warp.SetTransportAsync(s.Transport, ct);
            var own = EndpointList();
            if (own.Count > 0)
            {
                // подключаемся к тому из своих адресов, на котором стратегия последний раз прошла проверку
                string pinned = Config.Results.TryGetValue(s.Id, out var known) && known.Endpoint != null && own.Contains(known.Endpoint)
                    ? known.Endpoint : own[0];
                if (!await Warp.SetEndpointAsync(pinned, ct))
                {
                    Log.Write(L.T("err.endpoint"));
                    await StopAllAsync();
                    Set(EngineState.Idle, M("detail.connectFailed"));
                    return false;
                }
            }
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
            Proxy.Stop();
            _proxy = null;
            try
            {
                using (var limit = new CancellationTokenSource(10000))
                {
                    // С собственным сервером WARP не нужен: трогаем его, только если он остался подключённым от прошлого режима.
                    if (Warp.Installed && (!ProxyMode || await WarpIsActiveAsync(limit.Token)))
                    {
                        await Warp.DisconnectAsync(limit.Token);
                        if (!await Warp.SetEndpointAsync(null, limit.Token)) throw new IOException(L.T("err.endpoint"));
                    }
                }
            }
            finally { Zapret.Stop(); _activeStrategy = null; }
        }

        async Task<bool> WarpIsActiveAsync(CancellationToken ct)
        {
            var (status, _) = await Warp.StatusAsync(ct);
            return status == "Connected" || status == "Connecting";
        }

        async Task<bool> TryStopAllAsync()
        {
            try { await StopAllAsync(); return true; }
            catch (Exception e) { Log.Write(L.T("log.error", e.Message)); return false; }
        }

        Msg DescribeSelected()
        {
            var s = Selected;
            return s == null ? M(ProxyMode ? "detail.proxyConnected" : "detail.warpConnected") : M("detail.strategy", s);
        }

        // ------------------------------------------------------------------ собственный сервер

        int ProxyTimeoutMs() => Math.Max(6, Math.Min(60, Config.TestTimeoutSec)) * 1000;

        /// <summary>Подробность неудачи (текст сетевой ошибки и последняя ошибка sing-box) - в журнал, а не в список результатов.</summary>
        void LogProxyDetail(ProxyMeasure measure)
        {
            if (!string.IsNullOrWhiteSpace(measure.Error)) Log.Write("  " + L.T("log.proxyDetail", measure.Error));
            string box = Proxy.LastError();
            if (!string.IsNullOrWhiteSpace(box)) Log.Write("  " + L.T("log.proxyDetail", box));
        }

        /// <summary>
        /// Сессия sing-box для сервера из ссылки. Имя сервера разрешается один раз за операцию, пока системный DNS
        /// ещё работает мимо туннеля. С адаптером добавляются правила маршрутизации и выбор программ.
        /// </summary>
        async Task<ProxySession> NewProxySessionAsync(ProxyProfile profile, bool tun, CancellationToken ct)
        {
            if (_proxyAddress == null) _proxyAddress = await Proxy.ResolveAsync(profile.Host, ct);
            var session = Proxy.NewSession(profile);
            session.Address = _proxyAddress;
            session.Tun = tun;
            session.Ipv6 = Config.ProxyIpv6;
            session.Dns = Config.ProxyDns;
            if (tun)
            {
                session.Rules = CompileRules(Config) ?? new List<object>();
                if (Config.PerAppProxy)
                {
                    session.AppPaths = SelectedAppPaths();
                    session.OwnPath = OwnExecutable();
                }
            }
            return session;
        }

        /// <summary>Проверить собственный сервер: поднять sing-box без адаптера и сделать запросы через проверочный вход.</summary>
        async Task<TestResult> TestProxyAsync(Strategy s, CancellationToken ct)
        {
            var res = new TestResult { StrategyId = s.Id, When = DateTime.Now, Endpoint = s.Profile.Endpoint };
            ProxySession session;
            try { session = await NewProxySessionAsync(s.Profile, tun: false, ct); }
            catch (IOException e) { res.Fail(M("err.proxyFailed", e.Message)); return res; }
            var err = await Proxy.StartAsync(session, ct);
            if (err != null) { res.Fail(err); return res; }
            try
            {
                var m = await Proxy.MeasureAsync(session, 3, ProxyTimeoutMs(), ct);
                if (!m.Ok)
                {
                    res.Fail(M("err.proxyNoTraffic"));
                    LogProxyDetail(m);
                    return res;
                }
                res.Ok = true;
                res.ConnectMs = m.FirstMs;
                res.PingMs = m.PingMs;
                return res;
            }
            finally { Proxy.Stop(); }
        }

        /// <summary>
        /// Подключиться через собственный сервер. Сначала сервер проверяется без адаптера: неработающий сервер не должен
        /// забрать весь трафик, а потом sing-box поднимается заново уже с виртуальным адаптером Zarp.
        /// </summary>
        async Task<bool> ApplyProxyAsync(Strategy s, CancellationToken ct)
        {
            await StopAllAsync();
            bool Fail(Msg why)
            {
                Log.Write(why.ToString());
                Proxy.Stop();
                _proxy = null;
                Set(EngineState.Idle, M("detail.connectFailed"));
                return false;
            }

            ProxySession probe;
            try { probe = await NewProxySessionAsync(s.Profile, tun: false, ct); }
            catch (IOException e) { return Fail(M("err.proxyFailed", e.Message)); }
            var err = await Proxy.StartAsync(probe, ct);
            if (err != null) return Fail(err);
            var check = await Proxy.MeasureAsync(probe, 1, ProxyTimeoutMs() * 2, ct);
            if (!check.Ok) LogProxyDetail(check);
            Proxy.Stop();
            if (!check.Ok) return Fail(M("err.proxyNoTraffic"));

            var session = await NewProxySessionAsync(s.Profile, tun: true, ct);
            err = await Proxy.StartAsync(session, ct);
            if (err != null) return Fail(err);
            if (!await Proxy.WaitAdapterAsync(ct)) return Fail(M("err.singboxAdapter"));
            // адаптер уже забрал весь трафик: если через него ничего не идёт, лучше сразу его убрать, чем оставить без сети
            if (!await Proxy.CheckTunnelAsync(ProxyTimeoutMs(), ct)) return Fail(M("err.tunNoTraffic"));
            var done = await Proxy.MeasureAsync(session, 1, ProxyTimeoutMs() * 2, ct);
            if (!done.Ok) return Fail(M("err.proxyNoTraffic"));
            ct.ThrowIfCancellationRequested();
            _proxy = session;
            Log.Write(L.T("log.proxyConnected", check.FirstMs, done.PingMs, s.Profile.Endpoint));
            Set(EngineState.Connected, M("detail.strategy", s));
            return true;
        }
    }
}
