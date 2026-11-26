using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace HotkeyApp
{
    /// <summary>
    /// Палитра команд (как PowerToys Command Palette): вызывается глобальным
    /// хоткеем, ищет по встроенным командам и приложениям из панели запуска,
    /// отправляет ДСЕ на бэкенд. Все настройки — в отдельном окне SettingsWindow
    /// (кнопка ⚙ или команда «Настройки»).
    /// </summary>
    public partial class MainWindow : Window
    {
        private BackendClient? _backend; // создаётся после запуска локального модуля
        private AppSettings _settings = SettingsService.Load();

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

        private static readonly (string Title, string Text)[] BuiltInCommands =
        {
            ("Отчёт 1010: введите ДСЕ через $", ""),
            ("Отчёт 2012: введите ДСЕ через $", ""),
            ("Настройки", "@settings"),
        };

        public MainWindow()
        {
            InitializeComponent();

            IntPtr handle = new WindowInteropHelper(this).EnsureHandle();
            _source = HwndSource.FromHwnd(handle);
            _source.AddHook(HwndHook);

            bool hotkeyRegistered = ReregisterHotkey();
            if (!hotkeyRegistered)
            {
                MessageBox.Show(
                    $"Не удалось зарегистрировать хоткей " +
                    $"{_settings.HotkeyModifiers}+{_settings.HotkeyKey}.\n" +
                    "Возможно, комбинация занята. Выберите другую в настройках (⚙).",
                    "Хоткей", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            ApplyWindowModeFromSettings();

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
                StatusText.Text = "";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Модуль ManagerDataBase не запущен — откройте ⚙ → Модули";
                Debug.WriteLine("InitializeModulesAsync: " + ex);
            }
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

        private void OnHotkeyPressed()
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
            }));
        }

        // ---------------- Связь с окном настроек ----------------

        /// <summary>Перечитывает конфиг с диска (после изменений в окне настроек).</summary>
        public void ReloadSettings()
        {
            _settings = SettingsService.Load();
        }

        /// <summary>Применяет режим окна из текущих настроек.</summary>
        public void ApplyWindowModeFromSettings()
        {
            Topmost = _settings.Topmost;
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e) => OpenSettings();

        private void OpenSettings()
        {
            if (_settingsWindow == null || !_settingsWindow.IsLoaded)
            {
                _settingsWindow = new SettingsWindow { Owner = this };
            }
            _settingsWindow.Show();
            _settingsWindow.Activate();
        }

        // ---------------- Поведение окна ----------------

        private void Window_Deactivated(object sender, EventArgs e)
        {
            // Не прячем палитру, пока открыто окно настроек
            if (_settingsWindow != null && _settingsWindow.IsVisible)
                return;

            // В «обычном закреплённом» режиме окно НЕ прячется при потере фокуса
            if (_settings.StayOpenOnLostFocus)
                return;

            // Игнорируем ложное событие сразу после активации
            if ((DateTime.Now - _lastActivateTime).TotalMilliseconds < 500)
                return;

            CommandsPopup.IsOpen = false;
            this.Visibility = Visibility.Hidden;
            this.ShowInTaskbar = false;
        }

        // ---------------- Выпадающий список команд + выполнение ----------------

        private void MyTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateCommandPalette();
        }

        private void UpdateCommandPalette()
        {
            if (MyTextBox == null || CommandsPopup == null || CommandsList == null)
                return;

            var query = MyTextBox.Text?.Trim() ?? "";
            if (query.Length == 0 || query.StartsWith("Введите"))
            {
                CommandsPopup.IsOpen = false;
                return;
            }

            var items = new List<string>();

            // встроенные команды
            items.AddRange(BuiltInCommands
                .Where(c => c.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(c => c.Title));

            // приложения из панели запуска
            items.AddRange(_settings.LaunchItems
                .Where(i => i.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(i => $"▶ {i.Name}"));

            if (items.Count == 0)
            {
                CommandsPopup.IsOpen = false;
                return;
            }

            CommandsList.ItemsSource = items;
            CommandsList.SelectedIndex = 0;
            CommandsPopup.IsOpen = true;
        }

        private void MyTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (CommandsPopup.IsOpen)
            {
                switch (e.Key)
                {
                    case Key.Escape:
                        CommandsPopup.IsOpen = false;
                        e.Handled = true;
                        return;
                    case Key.Down:
                        CommandsList.SelectedIndex =
                            Math.Min(CommandsList.SelectedIndex + 1, CommandsList.Items.Count - 1);
                        CommandsList.ScrollIntoView(CommandsList.SelectedItem);
                        e.Handled = true;
                        return;
                    case Key.Up:
                        CommandsList.SelectedIndex = Math.Max(CommandsList.SelectedIndex - 1, 0);
                        CommandsList.ScrollIntoView(CommandsList.SelectedItem);
                        e.Handled = true;
                        return;
                }
            }

            if (e.Key == Key.Enter)
            {
                CommandsPopup.IsOpen = false;
                ExecuteInput(MyTextBox.Text);
                e.Handled = true;
            }
        }

        private void CommandsList_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                CommandsPopup.IsOpen = false;
                ExecuteInput(MyTextBox.Text);
                e.Handled = true;
            }
        }

        private void CommandsList_MouseUp(object sender, MouseButtonEventArgs e)
        {
            var selected = CommandsList.SelectedItem as string;
            if (selected != null)
            {
                MyTextBox.Text = selected;
                MyTextBox.CaretIndex = selected.Length;
            }
            CommandsPopup.IsOpen = false;
            ExecuteInput(selected ?? MyTextBox.Text);
        }

        /// <summary>
        /// Если введённая строка совпадает с названием команды/приложения — выполняем её,
        /// иначе отправляем ДСЕ на бэкенд.
        /// </summary>
        private async void ExecuteInput(string input)
        {
            input = input?.Trim() ?? "";
            if (input.Length == 0) return;

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

            // выбор отчёта из встроенных команд
            int program = _settings.DefaultProgram;
            if (input.Contains("2012", StringComparison.OrdinalIgnoreCase)) program = 2012;
            if (input.Contains("1010", StringComparison.OrdinalIgnoreCase)) program = 1010;

            var dseList = input
                .Split(new[] { '$', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToList();

            // если ввод — это выбранная команда из палитры (без ДСЕ), не отправляем на бэкенд
            if (BuiltInCommands.Any(c => string.Equals(c.Title, input, StringComparison.OrdinalIgnoreCase)))
                return;
            if (dseList.Count == 0) return;

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
                MessageBox.Show($"Готово: обработано {result.Processed} ДСЕ (отчёт {result.Program}).",
                                "ManagerDataBase", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
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
