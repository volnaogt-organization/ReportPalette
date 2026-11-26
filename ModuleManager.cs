using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace HotkeyApp
{
    /// <summary>Описание модуля из modules.json (лежит рядом с HotkeyApp.exe).</summary>
    public class ModuleDef
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("display_name")] public string DisplayName { get; set; } = "";
        [JsonPropertyName("repo")] public string Repo { get; set; } = "";
        [JsonPropertyName("port")] public int Port { get; set; }
        [JsonPropertyName("entry")] public string Entry { get; set; } = "service.api:app";
    }

    /// <summary>Снимок состояния модуля для окна настроек.</summary>
    public class ModuleStatus
    {
        public ModuleDef Def { get; set; } = new();
        public string? InstalledVersion { get; set; }
        public string? LatestVersion { get; set; }
        public bool Running { get; set; }
        public bool UpdateAvailable =>
            InstalledVersion != null && LatestVersion != null &&
            ModuleManager.CompareVersions(LatestVersion, InstalledVersion) > 0;
    }

    /// <summary>
    /// Менеджер модулей ReportPalette.
    ///
    /// Модули (ManagerDataBase и будущие) — Python-сервисы из релизов GitHub.
    /// Всё хозяйство живёт в %LOCALAPPDATA%\ReportPalette:
    ///   runtime\python\            — портабельный Python (скачивается один раз, ~15 МБ)
    ///   modules\&lt;имя&gt;\app\   — код модуля (релизный zipball репозитория)
    ///   modules\&lt;имя&gt;\app\.libs — зависимости модуля (pip install -t)
    ///   modules\&lt;имя&gt;\logs\module.log — лог сервиса модуля
    ///
    /// Модуль запускается дочерним процессом: python -m uvicorn &lt;entry&gt; --port N,
    /// общение с ним — по HTTP на 127.0.0.1. Обновление — скачивание последнего
    /// релиза с GitHub, установка зависимостей, подмена папки app (с откатом).
    /// </summary>
    public static class ModuleManager
    {
        private const string PythonVersion = "3.12.7";
        private const string PythonZipUrl =
            "https://www.python.org/ftp/python/" + PythonVersion + "/python-" + PythonVersion + "-embed-amd64.zip";
        private const string GetPipUrl = "https://bootstrap.pypa.io/get-pip.py";

        public static readonly string RootDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ReportPalette");

        private static string RuntimePythonDir => Path.Combine(RootDir, "runtime", "python");
        private static string PythonExe => Path.Combine(RuntimePythonDir, "python.exe");
        private static string ModulesDir => Path.Combine(RootDir, "modules");
        private static string ModuleDir(ModuleDef d) => Path.Combine(ModulesDir, d.Name);
        private static string ModuleAppDir(ModuleDef d) => Path.Combine(ModuleDir(d), "app");
        public static string ModuleLogPath(ModuleDef d) => Path.Combine(ModuleDir(d), "logs", "module.log");

        private static readonly HttpClient Http = CreateHttp();
        private static readonly HttpClient HttpShort = CreateHttp(TimeSpan.FromSeconds(2));

        private static readonly object _childrenLock = new();
        private static readonly Dictionary<string, Process> _children = new();
        private static readonly Dictionary<string, SemaphoreSlim> _locks = new();
        private static readonly object _logLock = new();
        private static List<ModuleDef>? _defs;

        private static HttpClient CreateHttp(TimeSpan? timeout = null)
        {
            var h = new HttpClient();
            h.DefaultRequestHeaders.UserAgent.ParseAdd("ReportPalette/1.0"); // GitHub API требует User-Agent
            h.Timeout = timeout ?? TimeSpan.FromMinutes(10); // скачивание релизов может быть медленным
            return h;
        }

        // ---------------------------------------------------------- реестр ----

        /// <summary>Читает modules.json рядом с exe; при отсутствии создаёт с модулем по умолчанию.</summary>
        public static List<ModuleDef> LoadDefs()
        {
            if (_defs != null) return _defs;

            var path = Path.Combine(AppContext.BaseDirectory, "modules.json");
            if (!File.Exists(path))
            {
                const string defaultJson = """
                {
                  "modules": [
                    {
                      "name": "managerdatabase",
                      "display_name": "ManagerDataBase",
                      "repo": "volnaogt-organization/ManagerDataBase",
                      "port": 8100,
                      "entry": "service.api:app"
                    }
                  ]
                }
                """;
                try { File.WriteAllText(path, defaultJson); } catch { /* нет прав — просто используем дефолт */ }
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var defs = new List<ModuleDef>();
                foreach (var el in doc.RootElement.GetProperty("modules").EnumerateArray())
                    defs.Add(JsonSerializer.Deserialize<ModuleDef>(el.GetRawText()) ?? new ModuleDef());
                _defs = defs.Where(d => d.Name.Length > 0 && d.Repo.Length > 0 && d.Port > 0).ToList();
            }
            catch
            {
                _defs = new List<ModuleDef>();
            }
            return _defs;
        }

        public static ModuleDef GetDef(string name) =>
            LoadDefs().FirstOrDefault(d => d.Name == name)
            ?? throw new InvalidOperationException($"Модуль '{name}' не описан в modules.json");

        public static string GetModuleBaseAddress(string name) => $"http://127.0.0.1:{GetDef(name).Port}/";

        // ---------------------------------------------------------- статус ----

        public static string? GetInstalledVersion(ModuleDef d)
        {
            try
            {
                var f = Path.Combine(ModuleAppDir(d), "version.txt");
                return File.Exists(f) ? File.ReadAllText(f).Trim() : null;
            }
            catch { return null; }
        }

        public static async Task<bool> IsRunningAsync(ModuleDef d)
        {
            try
            {
                var resp = await HttpShort.GetAsync(GetModuleBaseAddress(d.Name) + "health");
                return resp.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        /// <summary>Состояние всех модулей: установленная версия, последний релиз, запущен ли.</summary>
        public static async Task<List<ModuleStatus>> GetStatusAllAsync(CancellationToken ct = default)
        {
            var result = new List<ModuleStatus>();
            foreach (var def in LoadDefs())
            {
                var rel = await GetLatestReleaseAsync(def.Repo, ct);
                result.Add(new ModuleStatus
                {
                    Def = def,
                    InstalledVersion = GetInstalledVersion(def),
                    LatestVersion = rel.Tag,
                    Running = await IsRunningAsync(def),
                });
            }
            return result;
        }

        /// <summary>Сравнение версий вида v0.5.2 / 0.5.10: &gt;0, если a новее b.</summary>
        public static int CompareVersions(string? a, string? b)
        {
            static int[] Parse(string? v)
            {
                if (string.IsNullOrWhiteSpace(v)) return new[] { 0 };
                return v.Trim().TrimStart('v', 'V').Split('.', '-', '+')
                        .Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
            }
            var pa = Parse(a); var pb = Parse(b);
            for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
            {
                int xa = i < pa.Length ? pa[i] : 0;
                int xb = i < pb.Length ? pb[i] : 0;
                if (xa != xb) return xa.CompareTo(xb);
            }
            return 0;
        }

        // ------------------------------------------------------- жизненный цикл ----

        /// <summary>Гарантирует, что модуль установлен и запущен (при первом запуске — скачивает).</summary>
        public static async Task EnsureStartedAsync(string name, IProgress<string>? progress = null, CancellationToken ct = default)
        {
            var def = GetDef(name);
            var sem = LockFor(name);
            await sem.WaitAsync(ct);
            try
            {
                if (!Directory.Exists(ModuleAppDir(def)))
                    await InstallOrUpdateLockedAsync(def, progress, ct); // сама запустит
                else if (!await IsRunningAsync(def))
                    await StartAndWaitAsync(def, progress, ct);
            }
            finally { sem.Release(); }
        }

        /// <summary>Обновляет модуль до последнего релиза GitHub (и перезапускает его).</summary>
        public static async Task<string> UpdateAsync(string name, IProgress<string>? progress = null, CancellationToken ct = default)
        {
            var def = GetDef(name);
            var sem = LockFor(name);
            await sem.WaitAsync(ct);
            try
            {
                return await InstallOrUpdateLockedAsync(def, progress, ct);
            }
            finally { sem.Release(); }
        }

        /// <summary>Обновляет все модули по очереди. Ошибки собираются в текст результата.</summary>
        public static async Task<List<string>> UpdateAllAsync(IProgress<string>? progress = null, CancellationToken ct = default)
        {
            var results = new List<string>();
            foreach (var def in LoadDefs())
            {
                try { results.Add($"{def.DisplayName}: {await UpdateAsync(def.Name, progress, ct)}"); }
                catch (Exception ex) { results.Add($"{def.DisplayName}: ошибка — {ex.Message}"); }
            }
            return results;
        }

        private static SemaphoreSlim LockFor(string name)
        {
            lock (_locks)
            {
                if (!_locks.TryGetValue(name, out var sem))
                    _locks[name] = sem = new SemaphoreSlim(1, 1);
                return sem;
            }
        }

        /// <summary>Скачивает последний релиз, ставит зависимости, подменяет app (с откатом), запускает.</summary>
        private static async Task<string> InstallOrUpdateLockedAsync(ModuleDef def, IProgress<string>? progress, CancellationToken ct)
        {
            progress?.Report($"[{def.DisplayName}] проверка последнего релиза…");
            var rel = await GetLatestReleaseAsync(def.Repo, ct);
            if (rel.Tag == null || rel.ZipballUrl == null)
                throw new InvalidOperationException(
                    $"В репозитории {def.Repo} нет ни одного релиза (или GitHub недоступен).");

            var current = GetInstalledVersion(def);
            if (current == rel.Tag && Directory.Exists(ModuleAppDir(def)))
            {
                if (!await IsRunningAsync(def))
                    await StartAndWaitAsync(def, progress, ct);
                return $"уже последняя версия ({rel.Tag})";
            }

            progress?.Report($"[{def.DisplayName}] скачивание релиза {rel.Tag}…");
            var staging = ModuleDir(def) + ".new-" + Guid.NewGuid().ToString("N")[..6];
            try
            {
                Directory.CreateDirectory(staging);
                using (var stream = await Http.GetStreamAsync(rel.ZipballUrl, ct))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
                    ExtractStrippingRoot(zip, staging);

                progress?.Report($"[{def.DisplayName}] установка зависимостей Python…");
                await EnsureRuntimeAsync(progress, ct);
                var reqs = Path.Combine(staging, "requirements.txt");
                if (File.Exists(reqs))
                {
                    await RunProcessAsync(
                        PythonExe,
                        $"-m pip install --no-warn-script-location --disable-pip-version-check -r \"{reqs}\" -t \"{Path.Combine(staging, ".libs")}\"",
                        staging, null, ct);
                }
                File.WriteAllText(Path.Combine(staging, "version.txt"), rel.Tag);

                // Подмена папки app с возможностью отката
                progress?.Report($"[{def.DisplayName}] перезапуск на версии {rel.Tag}…");
                StopModule(def.Name);
                var appDir = ModuleAppDir(def);
                var oldDir = appDir + ".old";
                if (Directory.Exists(oldDir)) Directory.Delete(oldDir, true);
                if (Directory.Exists(appDir)) Directory.Move(appDir, oldDir);
                Directory.Move(staging, appDir);
                staging = null!;

                try
                {
                    await StartAndWaitAsync(def, progress, ct);
                }
                catch
                {
                    // откат на предыдущую версию
                    StopModule(def.Name);
                    if (Directory.Exists(appDir)) Directory.Delete(appDir, true);
                    if (Directory.Exists(oldDir))
                    {
                        Directory.Move(oldDir, appDir);
                        await StartAndWaitAsync(def, progress, ct);
                    }
                    throw;
                }

                if (Directory.Exists(oldDir)) { try { Directory.Delete(oldDir, true); } catch { } }
                return $"обновлён: {current ?? "—"} → {rel.Tag}";
            }
            finally
            {
                if (staging != null && Directory.Exists(staging))
                {
                    try { Directory.Delete(staging, true); } catch { }
                }
            }
        }

        // ---------------------------------------------------------- процессы ----

        private static async Task StartAndWaitAsync(ModuleDef def, IProgress<string>? progress, CancellationToken ct)
        {
            progress?.Report($"[{def.DisplayName}] запуск сервиса…");
            await EnsureRuntimeAsync(progress, ct);

            var appDir = ModuleAppDir(def);
            var logDir = Path.GetDirectoryName(ModuleLogPath(def))!;
            Directory.CreateDirectory(logDir);

            var psi = new ProcessStartInfo
            {
                FileName = PythonExe,
                Arguments = $"-m uvicorn {def.Entry} --host 127.0.0.1 --port {def.Port}",
                WorkingDirectory = appDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            // appDir — чтобы импортировались scr/, service/; .libs — зависимости модуля
            psi.Environment["PYTHONPATH"] = appDir + Path.PathSeparator + Path.Combine(appDir, ".libs");
            var ver = GetInstalledVersion(def);
            if (ver != null) psi.Environment["MODULE_VERSION"] = ver;

            var p = Process.Start(psi)
                ?? throw new InvalidOperationException($"Не удалось запустить процесс модуля {def.Name}");
            lock (_childrenLock) _children[def.Name] = p;
            p.EnableRaisingEvents = true;
            p.Exited += (_, _) => { lock (_childrenLock) _children.Remove(def.Name); };

            Pump(p.StandardOutput, ModuleLogPath(def));
            Pump(p.StandardError, ModuleLogPath(def));

            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (await IsRunningAsync(def)) return;
                if (p.HasExited)
                    throw new InvalidOperationException(
                        $"Сервис модуля {def.Name} завершился с кодом {p.ExitCode}. Лог: {ModuleLogPath(def)}");
                await Task.Delay(1000, ct);
            }
            throw new TimeoutException(
                $"Модуль {def.Name} не ответил на /health за 60 c. Лог: {ModuleLogPath(def)}");
        }

        public static void StopModule(string name)
        {
            lock (_childrenLock)
            {
                if (_children.TryGetValue(name, out var p))
                {
                    try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
                    _children.Remove(name);
                }
            }
        }

        /// <summary>Останавливает все запущенные нами модули (вызывается при выходе из приложения).</summary>
        public static void StopAll()
        {
            lock (_childrenLock)
            {
                foreach (var p in _children.Values)
                {
                    try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
                }
                _children.Clear();
            }
        }

        private static void Pump(StreamReader reader, string logPath)
        {
            Task.Run(async () =>
            {
                try
                {
                    string? line;
                    while ((line = await reader.ReadLineAsync()) != null)
                    {
                        lock (_logLock)
                            File.AppendAllText(logPath,
                                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}{Environment.NewLine}");
                    }
                }
                catch { /* процесс завершён */ }
            });
        }

        private static async Task RunProcessAsync(
            string file, string args, string workDir,
            IDictionary<string, string>? env, CancellationToken ct)
        {
            var psi = new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                WorkingDirectory = workDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            if (env != null)
                foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;

            using var p = Process.Start(psi)
                ?? throw new InvalidOperationException($"Не удалось запустить {file}");
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0)
            {
                var tail = (await stderr + "\n" + await stdout);
                tail = tail.Length > 1500 ? tail[^1500..] : tail;
                throw new InvalidOperationException($"{Path.GetFileName(file)} {args} → код {p.ExitCode}:\n{tail}");
            }
        }

        // ---------------------------------------------------------- runtime ----

        /// <summary>Гарантирует наличие портабельного Python с pip (скачивает один раз).</summary>
        private static async Task EnsureRuntimeAsync(IProgress<string>? progress, CancellationToken ct)
        {
            if (File.Exists(PythonExe)) return;

            progress?.Report($"Скачивание портабельного Python {PythonVersion}…");
            Directory.CreateDirectory(RuntimePythonDir);

            var tmpZip = Path.Combine(Path.GetTempPath(), $"python-{PythonVersion}-embed-amd64.zip");
            if (!File.Exists(tmpZip))
            {
                var bytes = await Http.GetByteArrayAsync(PythonZipUrl, ct);
                await File.WriteAllBytesAsync(tmpZip, bytes, ct);
            }
            ZipFile.ExtractToDirectory(tmpZip, RuntimePythonDir, overwriteFiles: true);

            // В embed-дистрибутиве site-packages отключены: без правки ._pth pip не встанет
            foreach (var pth in Directory.GetFiles(RuntimePythonDir, "python*._pth"))
            {
                var text = await File.ReadAllTextAsync(pth, ct);
                await File.WriteAllTextAsync(pth, text.Replace("#import site", "import site"), ct);
            }

            progress?.Report("Установка pip…");
            var getPip = Path.Combine(RuntimePythonDir, "get-pip.py");
            await File.WriteAllBytesAsync(getPip, await Http.GetByteArrayAsync(GetPipUrl, ct), ct);
            await RunProcessAsync(PythonExe, "get-pip.py --no-warn-script-location", RuntimePythonDir, null, ct);
            try { File.Delete(getPip); } catch { }
        }

        // ---------------------------------------------------------- GitHub ----

        private static async Task<(string? Tag, string? ZipballUrl, string? HtmlUrl)> GetLatestReleaseAsync(
            string repo, CancellationToken ct)
        {
            try
            {
                var resp = await Http.GetAsync(
                    $"https://api.github.com/repos/{repo}/releases/latest", ct);
                if (!resp.IsSuccessStatusCode) return (null, null, null);

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                var root = doc.RootElement;
                string? Get(string prop) =>
                    root.TryGetProperty(prop, out var el) ? el.GetString() : null;
                return (Get("tag_name"), Get("zipball_url"), Get("html_url"));
            }
            catch { return (null, null, null); }
        }

        /// <summary>Распаковывает zipball GitHub, срезая корневую папку owner-repo-sha/.</summary>
        private static void ExtractStrippingRoot(ZipArchive zip, string destDir)
        {
            foreach (var entry in zip.Entries)
            {
                var full = entry.FullName.Replace('\\', '/');
                int slash = full.IndexOf('/');
                if (slash < 0) continue; // файлы в корне архива (не бывает у zipball)
                var rel = full[(slash + 1)..];
                if (rel.Length == 0) continue;

                var dest = Path.Combine(destDir, rel.Replace('/', Path.DirectorySeparatorChar));
                if (rel.EndsWith('/'))
                {
                    Directory.CreateDirectory(dest);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: true);
            }
        }
    }
}
