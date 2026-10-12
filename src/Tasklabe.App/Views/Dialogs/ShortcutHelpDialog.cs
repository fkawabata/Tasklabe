using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Tasklabe.App.Controls;
using Tasklabe.App.Services;
using Tasklabe.Core.Keyboard;

namespace Tasklabe.App.Views.Dialogs;

/// <summary>
/// キーの一覧（要件 F-KEY-03、UX 規約 UX-13、UX-17）。いまの割り当てを、キーが効く範囲の階層ごとに並べる。
/// </summary>
public static class ShortcutHelpDialog
{
    /// <summary>階層（見出し、説明、含める範囲）。</summary>
    private static readonly (string Title, string Description, string[] Categories, bool AreaKeys)[] Layers =
    [
        (Texts.Get("Keys_App"), Texts.Get("Keys_AppDescription"), [ShortcutCatalog.Global], false),
        (Texts.Get("Keys_Task"), Texts.Get("Keys_TaskDescription"), [ShortcutCatalog.Task], false),
        (Texts.Get("Keys_View"), Texts.Get("Keys_ViewDescription"), [ShortcutCatalog.Wbs, ShortcutCatalog.Kanban, ShortcutCatalog.Gantt], true),
        (Texts.Get("Keys_Layer"), Texts.Get("Keys_LayerDescription"), [ShortcutCatalog.Layer, ShortcutCatalog.Composer], false),
    ];

    public static async Task ShowAsync(XamlRoot root)
    {
        var keymap = App.Current.Services.Keymap;
        var caption = AppResources.Style("Text.Caption");
        var panel = new StackPanel { Spacing = 20, Width = 480, Margin = new Thickness(0, 0, 16, 0) };
        panel.Children.Add(new TextBlock
        {
            Text = Texts.Format("Keys_Intro", keymap.Display("app.palette")),
            Style = caption,
            TextWrapping = TextWrapping.Wrap,
        });

        foreach (var (title, description, categories, areaKeys) in Layers)
        {
            var section = new StackPanel { Spacing = 4 };
            section.Children.Add(new TextBlock { Text = title, Style = AppResources.Style("Text.Subtitle"), FontSize = 16 });
            section.Children.Add(new TextBlock { Text = description, Style = caption, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });

            var rows = new List<(string? Heading, string Keys, string Label)>();
            if (areaKeys)
            {
                // 移動・選択のキー（変えられない）は、選んでいるタスクの範囲の定義から、画面の中の階層に並べる
                rows.AddRange(ShortcutCatalog.Fixed.Where(f => f.Category == ShortcutCatalog.Task).Select(f => ((string?)Texts.Get("Keys_ListsHeading"), f.Keys, f.Label)));
            }

            foreach (var category in categories)
            {
                string? heading = categories.Length > 1 ? category : null;
                rows.AddRange(ShortcutCatalog.All.Where(d => d.Category == category).Select(d => (heading, keymap.Display(d.Id), d.Label)));
                if (category != ShortcutCatalog.Task)
                {
                    rows.AddRange(ShortcutCatalog.Fixed.Where(f => f.Category == category).Select(f => (heading, f.Keys, f.Label)));
                }
            }

            string? current = null;
            foreach (var (heading, keys, label) in rows)
            {
                if (heading is not null && heading != current)
                {
                    current = heading;
                    section.Children.Add(new TextBlock { Text = heading, Style = AppResources.Style("Text.BodyStrong"), Margin = new Thickness(0, 6, 0, 0) });
                }

                var grid = new Grid { ColumnSpacing = 12 };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
                var key = KeyCaps(keys.Length > 0 ? keys : Texts.Get("Settings_NoShortcut"));
                Grid.SetColumn(key, 1);
                grid.Children.Add(key);
                section.Children.Add(grid);
            }

            panel.Children.Add(section);
        }

        var dialog = AppDialog.Info(root, Texts.Get("Keys_Title"), new ScrollViewer { Content = panel, MaxHeight = 560 });
        await dialog.ShowAsync();
    }

    /// <summary>キーの表記を、キーの形の小さな枠で示す。</summary>
    internal static FrameworkElement KeyCaps(string keys) => new Border
    {
        Padding = new Thickness(6, 1, 6, 2),
        CornerRadius = AppResources.CornerRadius("Radius.Control"),
        BorderThickness = new Thickness(1),
        BorderBrush = ThemeResources.Brush("CardStrokeColorDefaultBrush"),
        Background = ThemeResources.Brush("SubtleFillColorSecondaryBrush"),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = keys, Style = AppResources.Style("Text.Caption") },
    };
}
