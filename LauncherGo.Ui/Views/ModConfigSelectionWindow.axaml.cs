using Avalonia.Controls;
using Avalonia.Interactivity;
using LauncherGo.Abstractions.Services.I18n;
using LauncherGo.Ui;

namespace LauncherGo.Ui.Views;

public partial class ModConfigSelectionWindow : Window
{
    private readonly bool _isChinese;

    public ModConfigSelectionWindow()
        : this([], true)
    {
    }

    public ModConfigSelectionWindow(IEnumerable<string> paths, bool isChinese)
    {
        _isChinese = isChinese;
        InitializeComponent();

        Title = T("选择模组配置文件", "Select mod configuration");
        TitleTextBlock.Text = T("选择要编辑的配置文件", "Select a configuration file to edit");
        HintTextBlock.Text = T("此模组存在多个配置文件。请选择其中一个。", "This mod has multiple configuration files. Select one to edit.");
        CancelButton.Content = T("取消", "Cancel");
        SelectButton.Content = T("编辑", "Edit");

        var items = paths
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(static path => new ConfigFileItem(path))
            .ToArray();
        ConfigFilesListBox.ItemsSource = items;
        if (items.Length > 0)
            ConfigFilesListBox.SelectedIndex = 0;
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

    private void OnSelectClick(object? sender, RoutedEventArgs e)
    {
        if (ConfigFilesListBox.SelectedItem is ConfigFileItem item)
        {
            Close(item.FullPath);
            return;
        }

        StatusTextBlock.Text = T("请选择一个配置文件。", "Select a configuration file.");
    }

    private void OnConfigFileDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e) => OnSelectClick(sender, e);

    private string T(string zh, string en)
    {
        try
        {
            return ServiceLocator.GetRequiredService<ILocalizationService>().Resolve(zh, en);
        }
        catch (InvalidOperationException)
        {
            return _isChinese ? zh : en;
        }
    }

    private sealed class ConfigFileItem
    {
        public ConfigFileItem(string fullPath)
        {
            FullPath = fullPath;
        }

        public string FullPath { get; }

        public override string ToString() => Path.GetFileName(FullPath);
    }
}
