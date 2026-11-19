using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace HotkeyApp
{
    public partial class MainWindow : Window
    {
        private readonly BackendClient _backend = new();
        private AppSettings _settings = SettingsService.Load();
        private readonly ObservableCollection<LaunchItem> _launchItems = new();

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

        private static readonly (string Title, string Text)[] BuiltInCommands =
        {
            ("Отчёт 1010: введите ДСЕ через $", ""),
            ("Отчёт 2012: введите ДСЕ через $", ""),
            ("Открыть панель настроек переноса", "@settings"),
        };

        public MainWindow()
        {
            InitializeComponent();

            _launchItems = new ObservableCollection<LaunchItem>(_settings.LaunchItems);
            LaunchItemsList.ItemsSource = _launchItems;

            IntPtr handle = new WindowInteropHelper(this).EnsureHandle();
            _source = HwndSource.FromHwnd(handle);
            _source.AddHook(HwndHook);

            bool hotkeyRegistered = RegisterConfiguredHotkeyWithResult(handle);
            if (!hotkeyRegistered)
            {
                MessageBox.Show(
                    $"Не удалось зарегистрировать хоткей " +
                    $"{_settings.HotkeyModifiers}+{_settings.HotkeyKey}.\n" +
                    "Возможно, комбинация занята. Выберите другую в поле «Хоткей» ниже.",
                    "Хоткей", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            ApplyWindowMode();

            // Прячем окно в фон только если хоткей зарегистрирован —
            // иначе пользователь не сможет открыть окно и выбрать другую комбинацию.
            if (hotkeyRegistered)
            {
                this.Visibility = Visibility.Hidden;
                this.ShowInTaskbar = false;
            }
        }
        private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            e.Handled = true; // блокируем обычный ввод текста в TextBox

            // Игнорируем нажатие только модификаторов (без основной клавиши)
            var mods = Keyboard.Modifiers;
            if (mods == ModifierKeys.None) return;

            var key = e.Key == Key.System ? e.SystemKey : e.Key;

            // Игнорируем сами модификаторы
            if (key == Key.LeftCtrl || key == Key.RightCtrl ||
                key == Key.LeftAlt  || key == Key.RightAlt  ||
                key == Key.LeftShift|| key == Key.RightShift||
                key == Key.LWin    || key == Key.RWin)
                return;

            // Формируем строку модификаторов для конфига
            var modStr = "";
            if (mods.HasFlag(ModifierKeys.Control)) modStr += "CTRL|";
            if (mods.HasFlag(ModifierKeys.Alt))     modStr += "ALT|";
            if (mods.HasFlag(ModifierKeys.Shift))   modStr += "SHIFT|";
            if (mods.HasFlag(ModifierKeys.Windows)) modStr += "WIN|";
            modStr = modStr.TrimEnd('|');

            // Сохраняем в настройки
            _settings.HotkeyModifiers = modStr;
            _settings.HotkeyKey = key.ToString();

            // Обновляем поле
            HotkeyBox.Text = $"{modStr}+{key}";

            // Перерегистрируем хоткей
            var handle = new WindowInteropHelper(this).Handle;
            UnregisterHotKey(handle, HOTKEY_ID);

            if (RegisterConfiguredHotkeyWithResult(handle))
            {
                SaveSettings();
            }
            else
            {
                MessageBox.Show(
                    $"Комбинация {modStr}+{key} уже занята другой программой или системой.\n" +
                    "Выберите другую.",
                    "Хоткей", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }


        private bool RegisterConfiguredHotkeyWithResult(IntPtr handle)
        {
            UnregisterHotKey(handle, HOTKEY_ID);

            uint mods = 0;
            foreach (var m in _settings.HotkeyModifiers.Split('|', StringSplitOptions.RemoveEmptyEntries))
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
            if (!Enum.TryParse<Key>(_settings.HotkeyKey, true, out var key))
            {
                // неизвестная клавиша — используем Space
                vk = 0x20;
            }
            else
            {
                vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            }

            return RegisterHotKey(handle, HOTKEY_ID, mods, vk);
        }


        private void RegisterConfiguredHotkey(IntPtr handle)
        {
            UnregisterHotKey(handle, HOTKEY_ID);

            uint mods = 0;
            foreach (var m in _settings.HotkeyModifiers.Split('|', StringSplitOptions.RemoveEmptyEntries))
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
            if (!Enum.TryParse<Key>(_settings.HotkeyKey, true, out var key))
            {
                MessageBox.Show($"Неизвестная клавиша в конфиге: '{_settings.HotkeyKey}'. " +
                                "Используется Win+Space.", "Хоткей",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            }

            if (!RegisterHotKey(handle, HOTKEY_ID, mods, vk))
            {
                // Win+Space почти всегда занят системой — фолбэк бесполезен.
                MessageBox.Show($"Не удалось зарегистрировать хоткей " +
                                $"{_settings.HotkeyModifiers}+{_settings.HotkeyKey}.\n" +
                                "Возможно, комбинация занята другим приложением.",
                                "Хоткей", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            UnregisterHotKey(handle, HOTKEY_ID);
            _source?.RemoveHook(HwndHook);
            SaveSettings();
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
            this.Visibility = Visibility.Visible;
            this.WindowState = WindowState.Normal;

            this.Activate();

            Dispatcher.BeginInvoke(new Action(() =>
            {
                MyTextBox.Focus();
                MyTextBox.SelectAll();
            }));
        }

        // ---------------- Пункт 3: два режима окна вместо стандартной строки Windows ----------------
        // Окно остаётся обычным (сворачивается кнопками Windows), но:
        //  • «Обычное окно» — не сворачивается при клике вне экрана (не прячется на Deactivated);
        //  • «Поверх всех окон» — Topmost = true.

        private void WindowMode_Changed(object sender, RoutedEventArgs e)
        {
            ApplyWindowMode();
            SaveSettings();
        }

        private void ApplyWindowMode()
        {
            if (NormalModeRadio == null || TopmostModeRadio == null) return; // до InitializeComponent

            bool topmost = TopmostModeRadio.IsChecked == true;
            Topmost = topmost;
            _settings.StayOpenOnLostFocus = NormalModeRadio.IsChecked == true;

            if (topmost)
            {
                NormalModeRadio.IsChecked = false;
                _settings.Topmost = true;
            }
            else
            {
                _settings.Topmost = false;
            }
        }

        private void Window_Deactivated(object sender, EventArgs e)
        {
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

        // ---------------- Пункт 5: выпадающий список команд + выполнение ----------------

        private void MyTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateCommandPalette();
        }

        private void UpdateCommandPalette()
        {
            if (MyTextBox == null || CommandsPopup == null || CommandsList == null ||
                BuiltInCommands == null || _launchItems == null)
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
            items.AddRange(_launchItems
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
        /// иначе отправляем ДСЕ на бэкенд (прежнее поведение).
        /// </summary>
        private async void ExecuteInput(string input)
        {
            input = input?.Trim() ?? "";
            if (input.Length == 0) return;

            // команда со спец-префиксом — раскрыть/скрыть блок переноса настроек
            if (input == "@settings")
            {
                FindAndToggleExpander(this);
                return;
            }

            // запуск добавленного приложения по имени
            var app = _launchItems.FirstOrDefault(i =>
                string.Equals(i.Name, input, StringComparison.OrdinalIgnoreCase));
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

            try
            {
                var result = await _backend.RunAsync(program: program, dse: dseList);
                MessageBox.Show($"Готово: обработано {result.Processed} ДСЕ (отчёт {result.Program}).",
                                "ManagerDataBase", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось выполнить запрос к бэкенду:\n{ex.Message}\n\n" +
                                "Убедитесь, что Python-сервис запущен (см. backend/README.md).",
                                "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void FindAndToggleExpander(DependencyObject root)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is Expander exp && (string)exp.Header is string h &&
                    h.Contains("Перенос настроек"))
                {
                    exp.IsExpanded = !exp.IsExpanded;
                    return;
                }
                FindAndToggleExpander(child);
            }
        }

        // ---------------- Пункт 4: панель запуска произвольных приложений ----------------

        private void AddLaunchItem_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Выберите исполняемый файл приложения",
                Filter = "Программы (*.exe)|*.exe|Все файлы (*.*)|*.*"
            };
            if (dlg.ShowDialog(this) != true) return;

            var item = new LaunchItem
            {
                Name = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName),
                Path = dlg.FileName,
                Arguments = ""
            };

            // даём пользователю переименовать и задать параметры
            var name = Prompt("Отображаемое название:", item.Name);
            if (name == null) return; // отмена
            item.Name = string.IsNullOrWhiteSpace(name) ? item.Name : name.Trim();

            var args = Prompt("Параметры запуска (можно оставить пустыми):", "");
            if (args == null) return;
            item.Arguments = args.Trim();

            _launchItems.Add(item);
            _settings.LaunchItems = _launchItems.ToList();
            SaveSettings();
        }

        private void RemoveLaunchItem_Click(object sender, RoutedEventArgs e)
        {
            if (((Button)sender).Tag is LaunchItem item)
            {
                _launchItems.Remove(item);
                _settings.LaunchItems = _launchItems.ToList();
                SaveSettings();
            }
        }

        private void LaunchItem_Click(object sender, RoutedEventArgs e)
        {
            if (((Button)sender).Tag is LaunchItem item)
                LaunchProcess(item.Path, item.Arguments);
        }

        private static void LaunchProcess(string path, string arguments)
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

        // ---------------- Пункты 1–2: конфиг + хеш-строка переноса ----------------

        private void GenerateSettingsHash_Click(object sender, RoutedEventArgs e)
        {
            _settings.LaunchItems = _launchItems.ToList();
            ExportHashBox.Text = SettingsService.ExportHashString(_settings);
        }

        private void CopySettings_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(ExportHashBox.Text))
                GenerateSettingsHash_Click(sender, e);
            try { Clipboard.SetText(ExportHashBox.Text); } catch { }
        }

        private void ImportSettingsHash_Click(object sender, RoutedEventArgs e)
        {
            if (SettingsService.ImportHashString(ImportHashBox.Text, out var error))
            {
                _settings = SettingsService.Load();
                _launchItems.Clear();
                foreach (var i in _settings.LaunchItems) _launchItems.Add(i);

                TopmostModeRadio.IsChecked = _settings.Topmost;
                NormalModeRadio.IsChecked = !_settings.Topmost;
                ApplyWindowMode();
                RegisterConfiguredHotkey(new WindowInteropHelper(this).Handle);

                MessageBox.Show("Настройки применены.", "Перенос настроек",
                                MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(error, "Ошибка импорта",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void SaveSettings()
        {
            _settings.LaunchItems = _launchItems.ToList();
            try { SettingsService.Save(_settings); } catch { }
        }

        // Мини-диалог ввода (без дополнительных зависимостей)
        private string Prompt(string message, string defaultValue)
        {
            var panel = new StackPanel { Margin = new Thickness(10) };
            panel.Children.Add(new TextBlock { Text = message, Margin = new Thickness(0, 0, 0, 5) });
            var tb = new TextBox { Text = defaultValue, MinWidth = 300 };
            panel.Children.Add(tb);

            var win = new Window
            {
                Title = "Панель запуска",
                Content = panel,
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                Topmost = true
            };

            tb.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) { win.DialogResult = true; }
                else if (e.Key == Key.Escape) { win.DialogResult = false; }
            };
            tb.Focus();
            tb.SelectAll();

            var result = win.ShowDialog();
            return result == true ? tb.Text.Trim() : string.Empty;
        }
    }
}
