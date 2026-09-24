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
        /// <summary>Короткий текст для UI: что сейчас происходит.</summary>
        public string Detail { get; private set; } = "";
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

        void Set(EngineState st, string detail = null)
        {
            State = st;
            if (detail != null) Detail = detail;
            Changed?.Invoke();
        }

        void SetDetail(string detail)
        {
            Detail = detail;
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
                    Log.Write($"Сохранённая стратегия «{s.Name}» не сработала, пробую другие проверенные.");
                    if (await ApplyFirstWorkingAsync(others, ct)) return;
                }
                Log.Write("Проверенные стратегии не сработали, ищу заново.");
            }
            await SearchAndApplyAsync(null, ct);
        });

        /// <summary>Перебрать стратегии, выбрать самую быструю и подключиться.</summary>
        public Task SearchAsync(IEnumerable<Strategy> only = null) => Run(async ct =>
        {
            if (!await PrepareAsync(ct)) return;
            await SearchAndApplyAsync(only?.ToList(), ct);
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
            Set(EngineState.Disconnecting, "Отключаю...");
            await StopAllAsync();
            Set(EngineState.Idle, "Отключено");
        });

        /// <summary>Выяснить текущее состояние при запуске программы.</summary>
        public async Task RefreshStateAsync()
        {
            // распаковать вшитый zapret2 заранее, чтобы первое подключение не ждало; ошибки (антивирус) разберёт PrepareAsync
            try { Zapret.ExtractEmbedded(); }
            catch (Exception e) { Log.Write("zapret2 пока не распакован: " + e.Message); }
            if (!Warp.Installed) { Set(EngineState.Idle, "Cloudflare WARP не установлен"); return; }
            var (status, _) = await Warp.StatusAsync();
            if (status == "Connected")
                Set(EngineState.Connected, DescribeSelected());
            else
                Set(EngineState.Idle, Selected != null ? "Стратегия: " + Selected.Name : "Стратегия ещё не выбрана");
        }

        // ------------------------------------------------------------------ фоновое обновление zapret2

        /// <summary>Запустить фоновую проверку обновлений zapret2: через минуту после старта и далее каждые 12 часов.</summary>
        public void StartBackgroundUpdates()
        {
            Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromMinutes(1)); // не мешаем запуску и автоподключению
                while (true)
                {
                    if (Config.AutoUpdateZapret) await CheckZapretUpdateAsync(false);
                    await Task.Delay(TimeSpan.FromHours(12));
                }
            });
        }

        /// <summary>Проверить и, если есть, скачать и применить новую версию zapret2.</summary>
        public async Task CheckZapretUpdateAsync(bool verbose)
        {
            string tag;
            try
            {
                tag = await Zapret.DownloadUpdateAsync(CancellationToken.None);
            }
            catch (Exception e)
            {
                Log.Write("Проверка обновлений zapret2 не удалась: " + e.Message);
                return;
            }
            if (tag == null)
            {
                if (verbose) Log.Write("zapret2 " + Zapret.Version + ": последняя версия.");
                return;
            }
            Log.Write($"Скачана новая версия zapret2 {tag}.");

            // применяем сразу, если ничего не делаем; иначе - при следующем запуске winws2
            if (!await _busy.WaitAsync(0))
            {
                Log.Write("Обновление применится при следующем подключении.");
                return;
            }
            try
            {
                var s = Selected;
                if (State == EngineState.Connected && s != null && s.UsesZapret && Zapret.Running)
                {
                    // Туннель WARP уже установлен, zapret нужен только для рукопожатия -
                    // поэтому winws2 можно перезапустить на новой версии, не разрывая подключение.
                    string err = await Zapret.StartAsync(s, Config.RestrictToWarpIps);
                    if (err != null) Log.Write("После обновления winws2 не запустился: " + err);
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

        /// <summary>Остановить всё при выходе из программы.</summary>
        public async Task ShutdownAsync()
        {
            Cancel();
            if (Config.DisconnectOnExit)
                await StopAllAsync();
        }

        async Task Run(Func<CancellationToken, Task> body)
        {
            if (!await _busy.WaitAsync(0)) return; // уже чем-то заняты
            _cts = new CancellationTokenSource();
            try
            {
                await body(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                Log.Write("Операция отменена.");
                await StopAllAsync();
                Set(EngineState.Idle, "Отменено");
            }
            catch (Exception e)
            {
                Log.Write("Ошибка: " + e.Message);
                Set(EngineState.Idle, "Ошибка: " + e.Message);
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
            Set(EngineState.Preparing, "Подготовка...");
            if (!Warp.Installed)
            {
                Log.Write("Не найден warp-cli.exe. Установите Cloudflare WARP: https://one.one.one.one/");
                Set(EngineState.Idle, "Cloudflare WARP не установлен");
                return false;
            }
            if (!Zapret.Installed || Zapret.EmbeddedIsNewer)
            {
                if (!await InstallZapretAsync(ct) && !Zapret.Installed) return false;
            }
            foreach (var other in Zapret.ForeignDpiTools())
                Log.Write("Внимание: запущен другой обходчик DPI, он может мешать: " + other);
            var vpns = NetCheck.ForeignVpnAdapters();
            if (vpns.Count > 0)
            {
                // трафик WARP уйдёт в чужой туннель, и zapret на него не повлияет - результаты поиска будут недостоверны
                foreach (var v in vpns)
                    Log.Write("Внимание: активен сторонний VPN, через него уходит трафик WARP: " + v);
                Log.Write("Выключите другой VPN, иначе WARP может не подключиться, а стратегии будут подобраны неправильно.");
                if (AskContinueWithVpn != null && !AskContinueWithVpn(vpns))
                {
                    Set(EngineState.Idle, "Выключите сторонний VPN");
                    return false;
                }
            }
            if (!await Warp.EnsureRegisteredAsync())
            {
                Set(EngineState.Idle, "Не удалось зарегистрировать WARP");
                return false;
            }
            return true;
        }

        async Task<bool> InstallZapretAsync(CancellationToken ct)
        {
            var progress = new Progress<string>(m => { Log.Write(m); SetDetail(m); });
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
                        Set(EngineState.Idle, "zapret2 заблокирован антивирусом");
                        return false;
                    }
                }
                catch (Exception e) when (!(e is OperationCanceledException))
                {
                    Log.Write("Не удалось скачать zapret2: " + e.Message);
                    Log.Write("Можно распаковать вручную: winws2.exe, cygwin1.dll, WinDivert.dll, WinDivert64.sys, lua\\, files\\fake\\ в " + Zapret.Dir);
                    Set(EngineState.Idle, "Не удалось скачать zapret2");
                    return false;
                }
            }
            Set(EngineState.Idle, "zapret2 заблокирован антивирусом");
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
            string err = await Zapret.StartAsync(s, Config.RestrictToWarpIps);
            if (err != null) { res.Error = err; return res; }

            await Warp.ConnectAsync();
            int connectMs = await Warp.WaitConnectedAsync(Config.TestTimeoutSec * 1000, ct);
            if (connectMs < 0) { res.Error = $"нет подключения за {Config.TestTimeoutSec} с"; return res; }

            int ping = await Warp.MeasureAsync(3, ct);
            if (ping < 0) { res.Error = "WARP подключён, но трафик через него не идёт"; return res; }

            res.Ok = true;
            res.ConnectMs = connectMs;
            res.PingMs = ping;
            return res;
        }

        async Task SearchAndApplyAsync(List<Strategy> only, CancellationToken ct)
        {
            var list = only ?? Strategies;
            int stopAfter = only != null ? 0 : Config.StopAfterWorking;
            var candidates = new List<(Strategy S, TestResult R)>();

            Log.Write($"Поиск стратегии: {list.Count} вариантов" + (stopAfter > 0 ? $", остановка после {stopAfter} рабочих" : ""));
            ProgressTotal = list.Count;
            ProgressDone = 0;

            try
            {
                // ---- фаза 1: быстрый перебор
                foreach (var s in list)
                {
                    ct.ThrowIfCancellationRequested();
                    Set(EngineState.Searching, $"{ProgressDone + 1}/{list.Count}: {s.Name}");
                    var r = await TestAsync(s, ct);
                    Config.Results[s.Id] = r;
                    ProgressDone++;
                    Log.Write(r.Ok
                        ? $"  ✔ {s.Name}: подключение {r.ConnectMs} мс, пинг {r.PingMs} мс"
                        : $"  ✘ {s.Name}: {r.Error}");
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
                    Log.Write($"Перепроверка {candidates.Count} кандидатов...");
                    ProgressDone = 0;
                    ProgressTotal = candidates.Count;
                    int k = 0;
                    foreach (var (s, r1) in candidates.OrderBy(c => c.R.Score).ToList())
                    {
                        ct.ThrowIfCancellationRequested();
                        Set(EngineState.Searching, $"Перепроверка {++k}/{candidates.Count}: {s.Name}");
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
                            Log.Write($"  ✔✔ {s.Name}: подтверждена (подключение {r2.ConnectMs} мс, пинг {r2.PingMs} мс)");
                        }
                        else
                        {
                            r2.Error = "не подтвердилась: " + r2.Error;
                            Config.Results[s.Id] = r2;
                            Log.Write($"  ✘ {s.Name}: {r2.Error}");
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
                    ? "Кандидаты не прошли перепроверку: вероятно, они сработали случайно. Попробуйте поиск ещё раз или увеличьте таймаут."
                    : "Ни одна стратегия не сработала. Попробуйте увеличить таймаут в настройках или добавить свои стратегии в " + StrategyCatalog.CustomFileName);
                Set(EngineState.Idle, "Рабочая стратегия не найдена");
                return;
            }

            ProgressTotal = 0;
            var best = confirmed[0];
            var br = Config.Results[best.Id];
            Log.Write($"Лучшая стратегия: {best.Name} (подключение {br.ConnectMs} мс, пинг {br.PingMs} мс)");
            if (!await ApplyFirstWorkingAsync(confirmed, ct))
                Set(EngineState.Idle, "Стратегии нашлись, но подключиться не удалось");
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
                Log.Write($"«{s.Name}» не подключилась, пробую следующую.");
            }
            return false;
        }

        void MarkFailed(Strategy s)
        {
            Config.Results[s.Id] = new TestResult
            {
                StrategyId = s.Id, When = DateTime.Now, Ok = false, Error = "не подключилась при применении",
            };
            Config.Save();
        }

        /// <summary>Подключиться с заданной стратегией.</summary>
        async Task<bool> ApplyAsync(Strategy s, CancellationToken ct)
        {
            Set(EngineState.Connecting, "Подключение: " + s.Name);
            Log.Write("Подключаюсь со стратегией: " + s.Name);

            await Warp.DisconnectAsync();
            await Warp.SetTransportAsync(s.Transport, ct);
            string err = await Zapret.StartAsync(s, Config.RestrictToWarpIps);
            if (err != null)
            {
                Log.Write(err);
                Set(EngineState.Idle, "Ошибка запуска winws2");
                return false;
            }
            await Warp.ConnectAsync();
            int ms = await Warp.WaitConnectedAsync(Math.Max(30, Config.TestTimeoutSec * 2) * 1000, ct);
            if (ms < 0)
            {
                Log.Write("WARP не подключился.");
                await StopAllAsync();
                Set(EngineState.Idle, "Не удалось подключиться");
                return false;
            }
            Log.Write($"WARP подключён за {ms} мс.");
            Set(EngineState.Connected, "Стратегия: " + s.Name);
            return true;
        }

        async Task StopAllAsync()
        {
            if (Warp.Installed) await Warp.DisconnectAsync();
            Zapret.Stop();
        }

        string DescribeSelected()
        {
            var s = Selected;
            if (s == null) return "WARP подключён";
            return "Стратегия: " + s.Name;
        }
    }
}
