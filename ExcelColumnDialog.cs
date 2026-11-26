using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace HotkeyApp
{
    /// <summary>
    /// Диалог выбора листа и столбца Excel для команд модуля с вводом excel_column.
    /// Листы и столбцы подгружаются из модуля (/excel/sheets, /excel/columns).
    /// </summary>
    public static class ExcelColumnDialog
    {
        private class ColumnItem
        {
            public int Index { get; set; }
            public string Name { get; set; } = "";
            public override string ToString() => $"{Index}. {Name}";
        }

        /// <summary>
        /// Спрашивает у пользователя лист и столбец файла.
        /// Возвращает null, если пользователь отменил выбор.
        /// </summary>
        public static async Task<(string Sheet, int ColumnIndex)?> PickAsync(
            Window owner, BackendClient client, string path, string title)
        {
            // 1. Листы файла
            var sheetsJson = await client.PostAsync(
                "/excel/sheets", new Dictionary<string, object?> { ["path"] = path });

            var sheets = new List<string>();
            if (sheetsJson.ValueKind == JsonValueKind.Object &&
                sheetsJson.TryGetProperty("sheets", out var arr))
                foreach (var s in arr.EnumerateArray())
                {
                    var name = s.GetString();
                    if (!string.IsNullOrEmpty(name)) sheets.Add(name);
                }

            if (sheets.Count == 0)
            {
                MessageBox.Show(owner, "В файле не найдено ни одного листа.",
                                title, MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
            }

            // 2. Окно выбора
            var sheetBox = new ComboBox
            {
                ItemsSource = sheets,
                SelectedIndex = 0,
                Margin = new Thickness(0, 2, 0, 8),
                MinWidth = 320
            };
            var columnBox = new ComboBox { Margin = new Thickness(0, 2, 0, 0), MinWidth = 320 };
            var okButton = new Button
            {
                Content = "OK",
                IsDefault = true,
                IsEnabled = false,
                MinWidth = 80,
                Margin = new Thickness(0, 0, 8, 0)
            };

            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(new TextBlock { Text = "Лист:" });
            panel.Children.Add(sheetBox);
            panel.Children.Add(new TextBlock { Text = "Столбец с ДСЕ:" });
            panel.Children.Add(columnBox);

            var win = new Window
            {
                Title = title,
                Content = panel,
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = owner,
                ResizeMode = ResizeMode.NoResize,
                Topmost = true
            };

            async Task LoadColumnsAsync(string sheet)
            {
                okButton.IsEnabled = false;
                columnBox.ItemsSource = null;
                try
                {
                    var json = await client.PostAsync(
                        "/excel/columns",
                        new Dictionary<string, object?> { ["path"] = path, ["sheet"] = sheet });

                    var columns = new List<ColumnItem>();
                    if (json.ValueKind == JsonValueKind.Object &&
                        json.TryGetProperty("columns", out var cols))
                        foreach (var c in cols.EnumerateArray())
                            columns.Add(new ColumnItem
                            {
                                Index = c.GetProperty("index").GetInt32(),
                                Name = c.GetProperty("name").GetString() ?? ""
                            });

                    columnBox.ItemsSource = columns;
                    if (columns.Count > 0)
                    {
                        columnBox.SelectedIndex = 0;
                        okButton.IsEnabled = true;
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(win, $"Не удалось прочитать столбцы листа:\n{ex.Message}",
                                    title, MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }

            sheetBox.SelectionChanged += async (_, _) =>
            {
                if (sheetBox.SelectedItem is string sheet)
                    await LoadColumnsAsync(sheet);
            };

            string? resultSheet = null;
            int resultColumn = 0;
            okButton.Click += (_, _) =>
            {
                if (sheetBox.SelectedItem is string sheet &&
                    columnBox.SelectedItem is ColumnItem col)
                {
                    resultSheet = sheet;
                    resultColumn = col.Index;
                    win.DialogResult = true;
                }
            };

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            buttons.Children.Add(okButton);
            buttons.Children.Add(new Button { Content = "Отмена", IsCancel = true, MinWidth = 80 });
            panel.Children.Add(buttons);

            // Первый лист выбран до подписки — грузим его столбцы вручную
            await LoadColumnsAsync(sheets[0]);

            if (win.ShowDialog() != true || resultSheet == null)
                return null;
            return (resultSheet, resultColumn);
        }
    }
}
