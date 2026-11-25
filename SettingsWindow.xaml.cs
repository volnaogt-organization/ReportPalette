using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace HotkeyApp
{
    /// <summary>
    /// Окно настроек: режим палитры, хоткей, панель запуска, перенос настроек.
    /// Открывается из палитры (MainWindow) кнопкой ⚙ или командой «Настройки».
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private AppSettings _settings = SettingsService.Load();
        private readonly ObservableCollection<LaunchItem> _launchItems = new();

        /// <summary>Палитра-владелец; через неё перерегистрируем хоткей и применяем режим окна.</summary>
        private MainWindow? Palette => Owner as MainWindow;

        public SettingsWindow()
        {
            InitializeComponent();

            _launchItems = new ObservableCollection<LaunchItem>(_settings.LaunchItems);
            LaunchItemsList.ItemsSource = _launchItems;

            HotkeyBox.Text = $"{_settings.HotkeyModifiers}+{_settings.HotkeyKey}";
            TopmostModeRadio.IsChecked = _settings.Topmost;
            NormalModeRadio.IsChecked = !_settings.Topmost;
        }

        // ---------------- Режим окна палитры ----------------

        private void WindowMode_Changed(object sender, RoutedEventArgs e)
        {
            ApplyWindowMode();
        }

        private void ApplyWindowMode()
        {
            if (NormalModeRadio == null || TopmostModeRadio == null) return; // до InitializeComponent

            bool topmost = TopmostModeRadio.IsChecked == true;
            _settings.Topmost = topmost;
            _settings.StayOpenOnLostFocus = NormalModeRadio.IsChecked == true;

            // Сначала сохраняем и обновляем конфиг палитры, затем применяем режим
            SaveSettings();
            Palette?.ApplyWindowModeFromSettings();
        }

        // ---------------- Хоткей ----------------

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

            // Перерегистрируем хоткей через палитру, передавая НОВЫЕ значения явно
            // (конфиг палитры ещё старый — он обновится при SaveSettings ниже)
            if (Palette != null && Palette.ReregisterHotkey(modStr, key.ToString()))
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

        // ---------------- Панель запуска ----------------

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
            SaveSettings();
        }

        private void RemoveLaunchItem_Click(object sender, RoutedEventArgs e)
        {
            if (((Button)sender).Tag is LaunchItem item)
            {
                _launchItems.Remove(item);
                SaveSettings();
            }
        }

        private void LaunchItem_Click(object sender, RoutedEventArgs e)
        {
            if (((Button)sender).Tag is LaunchItem item)
                MainWindow.LaunchProcess(item.Path, item.Arguments);
        }

        // ---------------- Перенос настроек: хеш-строка v2 ----------------

        private string[] GetCheckedSections()
        {
            var sections = new List<string>();
            if (SecHotkey.IsChecked == true) sections.Add(SettingsSections.Hotkey);
            if (SecWindow.IsChecked == true) sections.Add(SettingsSections.Window);
            if (SecLaunch.IsChecked == true) sections.Add(SettingsSections.Launch);
            if (SecPlugins.IsChecked == true) sections.Add(SettingsSections.Plugins);
            if (SecTasks.IsChecked == true) sections.Add(SettingsSections.Tasks);
            if (SecBackend.IsChecked == true) sections.Add(SettingsSections.Backend);
            if (SecGeneral.IsChecked == true) sections.Add(SettingsSections.General);
            return sections.ToArray();
        }

        private void GenerateSettingsHash_Click(object sender, RoutedEventArgs e)
        {
            var sections = GetCheckedSections();
            if (sections.Length == 0)
            {
                MessageBox.Show("Отметьте хотя бы одну секцию для копирования.",
                                "Перенос настроек", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            _settings.LaunchItems = _launchItems.ToList();
            ExportHashBox.Text = SettingsService.ExportHashString(_settings, sections);
        }

        private void CopySettings_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(ExportHashBox.Text))
                GenerateSettingsHash_Click(sender, e);
            try { Clipboard.SetText(ExportHashBox.Text); } catch { }
        }

        private void ImportSettingsHash_Click(object sender, RoutedEventArgs e)
        {
            var preview = SettingsService.PreviewImport(ImportHashBox.Text, out var error);
            if (preview == null)
            {
                MessageBox.Show(error, "Ошибка импорта",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Диалог предпросмотра: что в строке и как применять списки
            var decision = ShowImportPreviewDialog(preview);
            if (decision == null) return; // отмена

            if (SettingsService.ApplyImport(preview, mergeLists: decision.Value, out error))
            {
                _settings = SettingsService.Load();
                _launchItems.Clear();
                foreach (var i in _settings.LaunchItems) _launchItems.Add(i);

                TopmostModeRadio.IsChecked = _settings.Topmost;
                NormalModeRadio.IsChecked = !_settings.Topmost;
                ApplyWindowMode();
                Palette?.ReloadSettings();
                Palette?.ReregisterHotkey();

                MessageBox.Show("Настройки применены.", "Перенос настроек",
                                MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(error, "Ошибка импорта",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>
        /// Показывает, что содержится в хеш-строке, и спрашивает режим применения
        /// списков. Возвращает null при отмене, иначе: true — объединить списки,
        /// false — заменить.
        /// </summary>
        private bool? ShowImportPreviewDialog(ImportPreview preview)
        {
            var sb = new System.Text.StringBuilder();
            if (preview.IsV1)
                sb.AppendLine("Строка старого формата (v1) — содержит все настройки.");
            sb.AppendLine("Содержимое строки:");
            foreach (var s in preview.Sections)
            {
                var extra = s switch
                {
                    SettingsSections.Launch when preview.LaunchCount > 0 => $" ({preview.LaunchCount} шт.)",
                    SettingsSections.Plugins when preview.PluginCount > 0 => $" ({preview.PluginCount} шт.)",
                    SettingsSections.Tasks when preview.TaskCount > 0 => $" ({preview.TaskCount} шт.)",
                    _ => ""
                };
                sb.AppendLine($"  • {SettingsSections.DisplayName(s)}{extra}");
            }

            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(new TextBlock
            {
                Text = sb.ToString().TrimEnd(),
                Margin = new Thickness(0, 0, 0, 10),
                TextWrapping = TextWrapping.Wrap
            });

            RadioButton? mergeRadio = null;
            if (preview.HasLists)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "Для списков (приложения, плагины, задачи):",
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(0, 0, 0, 4)
                });
                mergeRadio = new RadioButton
                {
                    Content = "Объединить с текущими (приложения, плагины, задачи)",
                    IsChecked = true,
                    Margin = new Thickness(0, 0, 0, 2)
                };
                var replaceRadio = new RadioButton { Content = "Заменить текущие списки" };
                panel.Children.Add(mergeRadio);
                panel.Children.Add(replaceRadio);
            }

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            var win = new Window
            {
                Title = "Импорт настроек",
                Content = panel,
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                Topmost = true
            };
            var ok = new Button { Content = "Применить", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
            ok.Click += (_, _) => { win.DialogResult = true; };
            var cancel = new Button { Content = "Отмена", IsCancel = true, MinWidth = 80 };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);

            if (win.ShowDialog() != true) return null;
            return mergeRadio?.IsChecked == true; // если списков нет — значение не важно
        }

        // ---------------- Служебное ----------------

        private void SaveSettings()
        {
            _settings.LaunchItems = _launchItems.ToList();
            try { SettingsService.Save(_settings); } catch { }
            Palette?.ReloadSettings();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        // Мини-диалог ввода (без дополнительных зависимостей)
        private string? Prompt(string message, string defaultValue)
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
            return result == true ? tb.Text.Trim() : null;
        }
    }
}
