using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace HotkeyApp
{
    /// <summary>
    /// Палитра команд (как PowerToys Command Palette): вызывается глобальным
    /// хоткеем, ищет по встроенным командам и приложениям из панели запуска,
    /// отправляет ДСЕ на бэкенд. Список команд виден всегда (последние/частые,
    /// фильтруются по вводу). Окно без системных кнопок: прячется при клике
    /// вне него, либо закрепляется кнопкой 📌 на самой палитре. Все остальные
    /// настройки — в отдельном окне SettingsWindow (кнопка ⚙ или команда
    /// «Настройки»).
    ///
    /// Drag-and-drop: перенос файла/папки с рабочего стола или из Проводника
    /// на окно палитры открывает подтверждение выдачи доступа к директории.
    /// </summary>
    public partial class MainWindow : Window
    {
        private BackendClient? _backend; // legacy-клиент (совместимость со старым модулем)
        private AppSettings _settings = SettingsService.Load();

        /// <summary>Команды из манифестов (module.json) установленных модулей.</summary>
        private List<ModuleCommand> _moduleCommands = new();

        /// <summary>Выбранная команда, ожидающая ввода аргументов (например, списка ДСЕ).</summary>
        private ModuleCommand? _pendingCommand;

        private readonly Dictionary<string, BackendClient> _moduleClients = new();

        /// <summary>Открыт модальный диалог (файл, лист/столбец) — палитру не прятать.</summary>
        private bool _modalOpen;

        /// <summary>Пользователь навигировал по списку стрелками — Enter берёт выбранный пункт.</summary>
        private bool _listNavigated;

        private bool _isDragging; // чтобы окно не пряталось во время переноса файла (drag-and-drop)

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_WIN = 0x0008;
        private const int HOTKEY_ID = 9000;

        private HwndSource _source;
        private DateTime _lastActivateTime = DateTime.MinValue;
        private SettingsWindow? _settingsWindow;
        private TrayIconService? _tray;
        private IslandWindow? _island; // таблетка сверху экрана (drag-and-drop отчётов)

        public MainWindow()
        {
            InitializeComponent();

            IntPtr handle = new WindowInteropHelper(this).EnsureHandle();
            _source = HwndSource.FromHwnd(handle);
            _source.AddHook(HwndHook);

            ApplyWindowModeFromSettings();
            ApplyIslandFromSettings(); // остров: таблетка сверху экрана (вкл/выкл — в настройках)

            // Значок в трее: доступ к палитре, даже если хоткей занят/не зарегистрировался
            _tray = new TrayIconService();
            _tray.Install(ShowPalette, OpenSettings, () => Close());

            bool hotkeyRegistered = ReregisterHotkey();
            if (!hotkeyRegistered)
            {
                _tray.ShowBalloon(
                    "ReportPalette",
                    $"Хоткей {_settings.HotkeyModifiers}+{_settings.HotkeyKey} занят другой программой.\n" +
                    "Палитру можно открыть двойным кликом по значку в трее; " +
                    "другую комбинацию — в настройках (⚙).");
                MessageBox.Show(
                    $"Не удалось зарегистрировать хоткей " +
                    $"{_settings.HotkeyModifiers}+{_settings.HotkeyKey}.\n" +
                    "Возможно, комбинация занята. Выберите другую в настройках (⚙) " +
                    "или открывайте палитру двойным кликом по значку в трее.",
                    "Хоткей", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            // Прячем окно в фон только если хоткей зарегистрирован —
            // иначе пользователь не сможет открыть окно и выбрать другую комбинацию.
            if (hotkeyRegistered)
            {
                this.Visibility = Visibility.Hidden;
                this.ShowInTaskbar = false;
            }

            // Модули поднимаются в фоне: при первом запуске скачиваются
            // портабельный Python и последний релиз ManagerDataBase.
            _ = InitializeModulesAsync();

            // Фоновая проверка обновления самой программы
            _ = CheckAppUpdateAsync();
        }

        /// <summary>Если на GitHub есть релиз новее текущего — подсказка в статусе и трее.</summary>
        private string? _appUpdateHint;

        private async Task CheckAppUpdateAsync()
        {
            try
            {
                var rel = await SelfUpdater.CheckAsync();
                if (!rel.UpdateAvailable) return;

                _appUpdateHint = $"Доступно обновление программы {rel.Tag} — ⚙ → Обновление программы";
                if (StatusText.Text.Length == 0)
                    StatusText.Text = _appUpdateHint;
                _tray?.ShowBalloon("ReportPalette",
                    $"Доступно обновление {rel.Tag}.\nУстановить: ⚙ → Обновление программы.");
            }
            catch { /* проверка необязательная — молчим */ }
        }

        // ---------------- Модули ----------------

        /// <summary>
        /// Гарантирует, что модуль ManagerDataBase скачан и запущен локально,
        /// затем подключает к нему клиента. Прогресс и ошибки — в статус-строке.
        /// </summary>
        private async Task InitializeModulesAsync()
        {
            var progress = new Progress<string>(msg => StatusText.Text = msg);
            try
            {
                await ModuleManager.EnsureStartedAsync("managerdatabase", progress);
                _backend = new BackendClient(ModuleManager.GetModuleBaseAddress("managerdatabase"));
                ReloadModuleCommands();
                UpdateCommandPalette(); // команды появились — обновить список
                StatusText.Text = _appUpdateHint ?? "";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Модуль ManagerDataBase не запущен — откройте ⚙ → Модули";
                Debug.WriteLine("InitializeModulesAsync: " + ex);
            }
        }

        /// <summary>Перечитывает команды из манифестов модулей (после установки/обновления).</summary>
        public void ReloadModuleCommands()
        {
            _moduleCommands = ModuleCatalog.LoadCommands();
            _island?.RefreshCommands(); // и остров видит те же команды
            if (IsLoaded) UpdateCommandPalette();
        }

        // ---------------- Хоткей ----------------

        /// <summary>
        /// Перерегистрирует глобальный хоткей. Без аргументов — из текущего конфига
        /// палитры; окно настроек передаёт новые значения явно (ещё до сохранения).
        /// Возвращает false, если комбинация занята.
        /// </summary>
        public bool ReregisterHotkey(string? modifiers = null, string? keyName = null)
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            UnregisterHotKey(handle, HOTKEY_ID);

            uint mods = 0;
            foreach (var m in (modifiers ?? _settings.HotkeyModifiers)
                              .Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                switch (m.Trim().ToUpperInvariant())
                {
                    case "WIN":   mods |= MOD_WIN;     break;
                    case "CTRL":  mods |= MOD_CONTROL; break;
                    case "SHIFT": mods |= MOD_SHIFT;   break;
                    case "ALT":   mods |= MOD_ALT;     break;
                }
            }
            if (mods == 0) mods = MOD_WIN;

            uint vk = 0x20;
            if (Enum.TryParse<Key>(keyName ?? _settings.HotkeyKey, true, out var key))
                vk = (uint)KeyInterop.VirtualKeyFromKey(key);

            return RegisterHotKey(handle, HOTKEY_ID, mods, vk);
        }

        protected override void OnClosed(EventArgs e)
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            UnregisterHotKey(handle, HOTKEY_ID);
            _source?.RemoveHook(HwndHook);
            _settingsWindow?.Close();
            _island?.Close();
            _tray?.Dispose();
            ModuleManager.StopAll(); // гасим локальные сервисы модулей
            base.OnClosed(e);
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            try
            {
                const int WM_HOTKEY = 0x0312;
                if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
                {
                    OnHotkeyPressed();
                    handled = true;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString(), "Ошибка в обработчике хоткея");
            }
            return IntPtr.Zero;
        }

        private void OnHotkeyPressed() => ShowPalette();

        /// <summary>Показывает палитру и переносит фокус в поле ввода (хоткей, трей).</summary>
        public void ShowPalette()
        {
            _lastActivateTime = DateTime.Now;

            this.ShowInTaskbar = true;

            // Если окно ещё ни разу не показывалось (стартовало скрытым) —
            // обязателен Show(): Activate() до первого вывода окна на экран
            // бросает InvalidOperationException.
            if (!IsVisible)
            {
                Show();
            }
            else
            {
                this.Visibility = Visibility.Visible;
            }

            this.WindowState = WindowState.Normal;

            // Кратковременный Topmost — гарантированно выводит окно на передний план
            this.Topmost = true;
            this.Topmost = _settings.Topmost;

            this.Activate();

            Dispatcher.BeginInvoke(new Action(() =>
            {
                MyTextBox.Focus();
                MyTextBox.SelectAll();
                UpdateCommandPalette(); // сразу показываем последние/частые команды
            }));
        }

        // ---------------- Связь с окном настроек ----------------

        /// <summary>Перечитывает конфиг с диска (после изменений в окне настроек).</summary>
        public void ReloadSettings()
        {
            _settings = SettingsService.Load();
            UpdatePinButton();
        }

        /// <summary>Применяет режим окна из текущих настроек.</summary>
        public void ApplyWindowModeFromSettings()
        {
            Topmost = _settings.Topmost;
            UpdatePinButton();
        }

        /// <summary>Показывает или убирает остров по настройке (переключатель в ⚙).</summary>
        public void ApplyIslandFromSettings()
        {
            if (_settings.IslandEnabled)
            {
                if (_island == null || !_island.IsLoaded)
                    _island = new IslandWindow();
                if (!_island.IsVisible)
                    _island.Show();
            }
            else
            {
                _island?.Close();
                _island = null;
            }
        }

        // ---------------- Пин (закрепить поверх окон) ----------------

        private void PinButton_Click(object sender, RoutedEventArgs e)
        {
            bool pin = !(_settings.Topmost && _settings.StayOpenOnLostFocus);
            _settings.Topmost = pin;
            _settings.StayOpenOnLostFocus = pin;
            try { SettingsService.Save(_settings); } catch { }
            ApplyWindowModeFromSettings();
        }

        private void UpdatePinButton()
        {
            if (PinButton == null) return;
            bool pinned = _settings.Topmost && _settings.StayOpenOnLostFocus;
            PinButton.Content = pinned ? "📌" : "📍";
            PinButton.ToolTip = pinned
                ? "Палитра закреплена поверх окон — нажмите, чтобы открепить"
                : "Палитра прячется при клике вне её — нажмите, чтобы закрепить поверх окон";
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e) => OpenSettings();

        public void OpenSettings()
        {
            if (_settingsWindow == null || !_settingsWindow.IsLoaded)
            {
                _settingsWindow = new SettingsWindow { Owner = this };
            }
            _settingsWindow.Show();
            _settingsWindow.Activate();
        }

        // ---------------- Поведение окна ----------------

        /// <summary>Перетаскивание окна за пустой фон (системного заголовка нет).</summary>
        private void RootBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // тащим только если нажали не по полю ввода/кнопке/списку
            if (e.OriginalSource is Border or Grid)
            {
                try { DragMove(); } catch { }
            }
        }

        private void HidePalette()
        {
            this.Visibility = Visibility.Hidden;
            this.ShowInTaskbar = false;
        }

        private void Window_Deactivated(object sender, EventArgs e)
        {
            // Не прячем палитру, пока идёт перенос файла (drag-and-drop)
            if (_isDragging)
                return;

            // Не прячем палитру, пока открыт модальный диалог или окно настроек
            if (_modalOpen)
                return;
            if (_settingsWindow != null && _settingsWindow.IsVisible)
                return;

            // В закреплённом режиме окно НЕ прячется при потере фокуса
            if (_settings.StayOpenOnLostFocus)
                return;

            // Игнорируем ложное событие сразу после активации
            if ((DateTime.Now - _lastActivateTime).TotalMilliseconds < 500)
                return;

            HidePalette();
        }

        // ---------------- Drag-and-drop: выдача доступа к директории ----------------

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            _isDragging = true;
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
            e.Handled = true;
        }

        private void Window_DragLeave(object sender, DragEventArgs e)
        {
            // Перенос ушёл за пределы окна без Drop — сбрасываем флаг,
            // иначе окно перестанет прятаться при потере фокуса.
            _isDragging = false;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            _isDragging = false;
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

            var paths = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (paths == null || paths.Length == 0) return;

            // файл -> его директория, папка -> сама папка
            string dir = paths.Length == 1
                ? (Directory.Exists(paths[0]) ? paths[0] : Path.GetDirectoryName(paths[0]) ?? "")
                : Path.GetDirectoryName(paths[0]) ?? "";

            if (dir.Length == 0 || !Directory.Exists(dir)) return;

            // Всплывающее окно-подтверждение — «даёт доступ» к директории
            _modalOpen = true; // диалог крадёт фокус — палитру не прячем
            try
            {
                var result = MessageBox.Show(
                    $"Выдать доступ приложению к директории?\n\n{dir}",
                    "Разрешить доступ", MessageBoxButton.OKCancel,
                    MessageBoxImage.Question, MessageBoxResult.Cancel);

                if (result != MessageBoxResult.OK) return;

                _settings.AllowedDirectory = dir;
                SettingsService.Save(_settings);
                StatusText.Text = $"Доступ к директории выдан: {dir}";
                MessageBox.Show($"Доступ к директории выдан:\n{dir}",
                                "ReportPalette", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            finally { _modalOpen = false; }
        }

        // ---------------- Список команд + выполнение ----------------

        private void MyTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateCommandPalette();
        }

        /// <summary>Элемент списка: подпись + действие.</summary>
        private class PaletteEntry
        {
            public string Label { get; set; } = "";
            public Action Run { get; set; } = () => { };
        }

        private PaletteEntry EntryFor(ModuleCommand cmd, string prefix = "")
        {
            var suffix = cmd.Input == "dse_list" ? " — ДСЕ через $"
                       : cmd.Input == "excel_column" ? " — столбец из Excel (📂)"
                       : cmd.IsFileInput ? " — путь к файлу (📂)"
                       : "";
            return new PaletteEntry { Label = prefix + cmd.Title + suffix, Run = () => StartCommand(cmd) };
        }

        /// <summary>При пустом поле — последние и частые команды (история использования).</summary>
        private List<PaletteEntry> SuggestedEntries()
        {
            var items = new List<PaletteEntry>();
            foreach (var key in CommandHistory.GetSuggested())
            {
                var cmd = _moduleCommands.FirstOrDefault(c => c.Key == key);
                if (cmd != null)
                    items.Add(EntryFor(cmd, prefix: "↺ "));
            }
            if (items.Count == 0) // истории нет — просто первые команды модулей
                items.AddRange(_moduleCommands.Take(6).Select(c => EntryFor(c)));
            return items;
        }

        private void UpdateCommandPalette()
        {
            if (MyTextBox == null || CommandsList == null || CommandsPanel == null)
                return;

            var query = MyTextBox.Text?.Trim() ?? "";
            if (query.StartsWith("Введите")) query = ""; // текст-заглушка = пустой ввод

            List<PaletteEntry> items;
            if (query.Length == 0)
            {
                items = SuggestedEntries(); // пустое поле: последние и частые команды
            }
            else
            {
                items = new List<PaletteEntry>();

                // команды из манифестов модулей
                items.AddRange(_moduleCommands
                    .Where(c => c.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .Select(c => EntryFor(c)));

                // встроенная команда
                if ("Настройки".Contains(query, StringComparison.OrdinalIgnoreCase))
                    items.Add(new PaletteEntry { Label = "Настройки", Run = OpenSettings });

                // приложения из панели запуска
                items.AddRange(_settings.LaunchItems
                    .Where(i => i.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .Select(i => new PaletteEntry { Label = "▶ " + i.Name, Run = () => LaunchProcess(i.Path, i.Arguments) }));
            }

            _listNavigated = false;

            if (items.Count == 0)
            {
                CommandsList.ItemsSource = null;
                CommandsPanel.Visibility = Visibility.Collapsed;
                return;
            }

            CommandsList.ItemsSource = items;
            CommandsList.SelectedIndex = 0;
            CommandsPanel.Visibility = Visibility.Visible;
        }

        private void MyTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                if (_pendingCommand != null) // отмена выбранной команды, ожидающей ввода
                {
                    ClearPendingCommand();
                    StatusText.Text = "Выбор команды отменён";
                    MyTextBox.Text = "";
                }
                else
                {
                    HidePalette(); // ничего не выбрано — спрятать палитру
                }
                e.Handled = true;
                return;
            }

            if (CommandsList.Items.Count > 0)
            {
                switch (e.Key)
                {
                    case Key.Down:
                        CommandsList.SelectedIndex =
                            Math.Min(CommandsList.SelectedIndex + 1, CommandsList.Items.Count - 1);
                        CommandsList.ScrollIntoView(CommandsList.SelectedItem);
                        _listNavigated = true;
                        e.Handled = true;
                        return;
                    case Key.Up:
                        CommandsList.SelectedIndex = Math.Max(CommandsList.SelectedIndex - 1, 0);
                        CommandsList.ScrollIntoView(CommandsList.SelectedItem);
                        _listNavigated = true;
                        e.Handled = true;
                        return;
                }
            }

            if (e.Key == Key.Enter)
            {
                // стрелками выбрали пункт списка — выполняем его
                if (_listNavigated && CommandsList.SelectedItem is PaletteEntry entry)
                {
                    _listNavigated = false;
                    MyTextBox.Text = "";
                    entry.Run();
                }
                else
                {
                    ExecuteInput(MyTextBox.Text);
                }
                e.Handled = true;
            }
        }

        private void CommandsList_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && CommandsList.SelectedItem is PaletteEntry entry)
            {
                MyTextBox.Text = "";
                entry.Run();
                e.Handled = true;
            }
        }

        private void CommandsList_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (CommandsList.SelectedItem is PaletteEntry entry)
            {
                MyTextBox.Text = "";
                MyTextBox.Focus();
                entry.Run();
            }
        }

        /// <summary>
        /// Разбор ввода палитры: точное совпадение с командой модуля — запуск команды;
        /// ввод при выбранной команде — её аргументы; список ДСЕ «напрямую» —
        /// команда по умолчанию (как раньше, отчёт из настроек/маркера 1010/2012).
        /// </summary>
        private void ExecuteInput(string input)
        {
            input = input?.Trim() ?? "";
            if (input.Length == 0 || input.StartsWith("Введите")) return;

            // команда со спец-префиксом — открыть окно настроек
            if (input == "@settings" ||
                string.Equals(input, "Настройки", StringComparison.OrdinalIgnoreCase))
            {
                OpenSettings();
                return;
            }

            // запуск добавленного приложения по имени
            var app = _settings.LaunchItems.FirstOrDefault(i =>
                string.Equals(i.Name, input.TrimStart('▶', ' '), StringComparison.OrdinalIgnoreCase));
            if (app != null)
            {
                LaunchProcess(app.Path, app.Arguments);
                return;
            }

            // точное совпадение с командой модуля — запускаем её
            var cmd = _moduleCommands.FirstOrDefault(c =>
                string.Equals(c.Title, input, StringComparison.OrdinalIgnoreCase));
            if (cmd != null)
            {
                StartCommand(cmd);
                return;
            }

            // команда выбрана и ждёт ввода — текущий текст = её аргументы
            if (_pendingCommand != null)
            {
                var pending = _pendingCommand;
                if (pending.Input == "dse_list")
                {
                    var dse = ParseDse(input);
                    if (dse.Count == 0) return;
                    ClearPendingCommand();
                    MyTextBox.Text = "";
                    CommandHistory.Register(pending.Key);
                    _ = ExecuteModuleCommandAsync(pending, dse, null);
                }
                else if (pending.IsFileInput)
                {
                    var path = input.Trim('"', '\'');
                    if (path.Length == 0) return;
                    ClearPendingCommand();
                    MyTextBox.Text = "";
                    CommandHistory.Register(pending.Key);
                    _ = ExecuteModuleCommandAsync(pending, null, path);
                }
                return;
            }

            // сырой ввод ДСЕ без выбора команды — отчёт по умолчанию (как раньше)
            var dseList = ParseDse(input);
            if (dseList.Count == 0) return;

            int program = _settings.DefaultProgram;
            if (input.Contains("2012", StringComparison.OrdinalIgnoreCase)) program = 2012;
            if (input.Contains("1010", StringComparison.OrdinalIgnoreCase)) program = 1010;

            // ищем команду модуля с этим program в payload; запасной вариант — default dse_list
            var target = _moduleCommands.FirstOrDefault(c =>
                             c.Input == "dse_list" && c.Payload != null &&
                             c.Payload.TryGetValue("program", out var p) &&
                             p.ValueKind == JsonValueKind.Number && p.GetInt32() == program)
                         ?? _moduleCommands.FirstOrDefault(c => c.Input == "dse_list" && c.Default)
                         ?? _moduleCommands.FirstOrDefault(c => c.Input == "dse_list");

            if (target != null)
            {
                CommandHistory.Register(target.Key);
                _ = ExecuteModuleCommandAsync(target, dseList, null);
                return;
            }

            // команды модуля недоступны (модуль старой версии) — прежний путь
            _ = RunLegacyAsync(program, dseList);
        }

        private static List<string> ParseDse(string input) =>
            input.Split(new[] { '$', ';' }, StringSplitOptions.RemoveEmptyEntries)
                 .Select(s => s.Trim())
                 .Where(s => s.Length > 0)
                 .ToList();

        /// <summary>Старт команды модуля: фиксирует выбор и по типу ввода просит аргументы.</summary>
        private void StartCommand(ModuleCommand cmd)
        {
            if (cmd.Input == "dse_list")
            {
                _pendingCommand = cmd;
                MyTextBox.Text = "";
                MyTextBox.Focus();
                StatusText.Text = $"Выбрано: {cmd.Title} — {cmd.Hint ?? "введите ДСЕ через $"}; " +
                                  "Enter — выполнить, Esc — отмена";
            }
            else if (cmd.Input == "excel_column")
            {
                _ = StartExcelColumnCommandAsync(cmd);
            }
            else if (cmd.IsFileInput)
            {
                _pendingCommand = cmd;
                BrowseFileButton.Visibility = Visibility.Visible;
                MyTextBox.Text = "";
                MyTextBox.Focus();
                StatusText.Text = $"Выбрано: {cmd.Title} — введите путь к файлу или нажмите 📂; " +
                                  "Enter — выполнить, Esc — отмена";
            }
            else // none
            {
                CommandHistory.Register(cmd.Key);
                _ = ExecuteModuleCommandAsync(cmd, null, null);
            }
        }

        /// <summary>
        /// Команда «столбец из Excel»: диалог файла → диалог листа и столбца →
        /// чтение ДСЕ из столбца через модуль → выполнение команды с этим списком.
        /// </summary>
        private async Task StartExcelColumnCommandAsync(ModuleCommand cmd)
        {
            string? path = null;
            _modalOpen = true; // диалоги крадут фокус — палитру не прячем
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = cmd.Title,
                    Filter = !string.IsNullOrEmpty(cmd.Filter)
                        ? cmd.Filter
                        : "Excel (*.xlsx;*.xls;*.xlsm)|*.xlsx;*.xls;*.xlsm|Все файлы (*.*)|*.*"
                };
                if (dlg.ShowDialog(this) == true)
                    path = dlg.FileName;
            }
            finally { _modalOpen = false; }

            if (path == null)
            {
                StatusText.Text = $"[{cmd.Title}] выбор файла отменён";
                return;
            }

            StatusText.Text = $"[{cmd.Title}] читаю листы файла…";

            (string Sheet, int ColumnIndex)? pick;
            _modalOpen = true;
            try
            {
                pick = await ExcelColumnDialog.PickAsync(
                    this, ClientForModule(cmd.ModuleName), path, cmd.Title);
            }
            catch (Exception ex)
            {
                StatusText.Text = $"[{cmd.Title}] ошибка чтения файла";
                MessageBox.Show($"Не удалось прочитать файл:\n{ex.Message}",
                                cmd.Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            finally { _modalOpen = false; }

            if (pick == null)
            {
                StatusText.Text = $"[{cmd.Title}] выбор столбца отменён";
                return;
            }

            StatusText.Text = $"[{cmd.Title}] читаю столбец «{pick.Value.Sheet}»…";
            try
            {
                var json = await ClientForModule(cmd.ModuleName).PostAsync(
                    "/excel/dse_column",
                    new Dictionary<string, object?>
                    {
                        ["path"] = path,
                        ["sheet"] = pick.Value.Sheet,
                        ["column_index"] = pick.Value.ColumnIndex
                    });

                var dse = json.GetProperty("dse_list").EnumerateArray()
                              .Select(el => el.GetString() ?? "")
                              .Where(s => s.Length > 0)
                              .ToList();
                if (dse.Count == 0)
                {
                    StatusText.Text = $"[{cmd.Title}] в столбце нет значений";
                    return;
                }

                CommandHistory.Register(cmd.Key);
                await ExecuteModuleCommandAsync(cmd, dse, null);
            }
            catch (Exception ex)
            {
                StatusText.Text = $"[{cmd.Title}] ошибка чтения столбца";
                MessageBox.Show($"Не удалось прочитать столбец:\n{ex.Message}",
                                cmd.Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>Кнопка 📂: диалог выбора файла для команды, ожидающей путь.</summary>
        private void BrowseFileButton_Click(object sender, RoutedEventArgs e)
        {
            var cmd = _pendingCommand;
            if (cmd == null || !cmd.IsFileInput)
            {
                BrowseFileButton.Visibility = Visibility.Collapsed;
                return;
            }

            _modalOpen = true; // диалог крадёт фокус — палитру не прячем
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = cmd.Title,
                    Filter = !string.IsNullOrEmpty(cmd.Filter)
                        ? cmd.Filter
                        : "Excel (*.xlsx;*.xls)|*.xlsx;*.xls|Все файлы (*.*)|*.*"
                };
                if (dlg.ShowDialog(this) == true)
                {
                    MyTextBox.Text = dlg.FileName;
                    MyTextBox.CaretIndex = MyTextBox.Text.Length;
                    MyTextBox.Focus();
                    StatusText.Text = $"[{cmd.Title}] файл выбран: {System.IO.Path.GetFileName(dlg.FileName)} — Enter для выполнения";
                }
            }
            finally { _modalOpen = false; }
        }

        /// <summary>Сброс выбранной команды (после выполнения или по Esc).</summary>
        private void ClearPendingCommand()
        {
            _pendingCommand = null;
            BrowseFileButton.Visibility = Visibility.Collapsed;
        }

        /// <summary>Выполнение команды модуля; прогресс и результат — в статус-строке.</summary>
        private async Task ExecuteModuleCommandAsync(
            ModuleCommand cmd, IReadOnlyList<string>? dseList, string? filePath)
        {
            StatusText.Text = $"[{cmd.Title}] выполняется…";
            OpenResultButton.Visibility = Visibility.Collapsed;
            _lastResultFile = null;
            try
            {
                var client = ClientForModule(cmd.ModuleName);
                var json = await client.PostAsync(cmd.Endpoint, cmd.BuildPayload(dseList, filePath));
                StatusText.Text = $"[{cmd.Title}] {DescribeResult(json)}";
                MyTextBox.Text = "";
                UpdateResultButton(cmd);
            }
            catch (Exception ex)
            {
                StatusText.Text = $"[{cmd.Title}] ошибка";
                _modalOpen = true;
                try
                {
                    MessageBox.Show($"Команда «{cmd.Title}» завершилась с ошибкой:\n{ex.Message}",
                                    "Модули", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                finally { _modalOpen = false; }
            }
        }

        /// <summary>Excel-файл, который модуль пишет в свой рабочий каталог (как cmd_body).</summary>
        private string? _lastResultFile;

        /// <summary>Показывает кнопку «Открыть результат», если модуль записал Excel-отчёт.
        /// Имя файла модуль объявляет в module.json (result_file); без объявления —
        /// стандартный result_manager_database_reports.xlsx.</summary>
        private void UpdateResultButton(ModuleCommand cmd)
        {
            var dir = ModuleManager.GetInstalledAppDir(cmd.ModuleName);
            var fileName = string.IsNullOrEmpty(cmd.ResultFile)
                ? "result_manager_database_reports.xlsx"
                : cmd.ResultFile;
            var file = dir == null ? null : System.IO.Path.Combine(dir, fileName);
            _lastResultFile = file != null && System.IO.File.Exists(file) ? file : null;
            OpenResultButton.ToolTip = _lastResultFile ?? "";
            OpenResultButton.Visibility = _lastResultFile != null
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void OpenResultButton_Click(object sender, RoutedEventArgs e)
        {
            if (_lastResultFile == null) return;
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = _lastResultFile,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось открыть файл:\n{ex.Message}",
                                "Результат", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static string DescribeResult(JsonElement json)
        {
            if (json.ValueKind == JsonValueKind.Object)
            {
                if (json.TryGetProperty("processed", out var pr) &&
                    json.TryGetProperty("program", out var pg))
                    return $"готово, обработано ДСЕ: {pr.GetInt32()} (отчёт {pg.GetInt32()})";
                if (json.TryGetProperty("status", out var st))
                    return "статус: " + (st.GetString() ?? "ok");
            }
            return "готово";
        }

        private BackendClient ClientForModule(string name)
        {
            if (!_moduleClients.TryGetValue(name, out var client))
                _moduleClients[name] = client = new BackendClient(ModuleManager.GetModuleBaseAddress(name));
            return client;
        }

        /// <summary>Старый путь: модуль без манифеста команд (версии до появления module.json ui).</summary>
        private async Task RunLegacyAsync(int program, List<string> dseList)
        {
            if (_backend == null)
            {
                MessageBox.Show("Модуль ManagerDataBase ещё запускается или не установлен.\n" +
                                "Статус — внизу окна палитры; управление — в ⚙ → Модули.",
                                "Модули", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                var result = await _backend.RunAsync(program: program, dse: dseList);
                StatusText.Text = $"Готово: обработано {result.Processed} ДСЕ (отчёт {result.Program}).";
                MyTextBox.Text = "";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Ошибка выполнения";
                MessageBox.Show($"Не удалось выполнить запрос к модулю:\n{ex.Message}\n\n" +
                                "Проверьте состояние модуля в ⚙ → Модули.",
                                "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        public static void LaunchProcess(string path, string arguments)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    Arguments = arguments ?? "",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось запустить \"{path}\":\n{ex.Message}",
                                "Панель запуска", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}