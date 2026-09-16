using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using LauncherGo.Domains.Models;

namespace LauncherGo.Ui.Views;

public partial class LauncherMainWindow
{
    private void SetServerMapMaintenanceControls(bool enabled)
    {
        ServerMapEditorPanel.IsEnabled = enabled;
        ServerMapProfileComboBox.IsEnabled = enabled;
        ServerMapSaveButton.IsEnabled = enabled;
        ServerMapDeployButton.IsEnabled = enabled;
        ServerMapResetWebButton.IsEnabled = enabled;
        ServerMapToggleButton.IsEnabled = enabled;
        ServerMapRefreshButton.IsEnabled = enabled;
        ServerMapBackButton.IsEnabled = enabled;
        ServerMapOpenButton.IsEnabled = enabled;
        if (!enabled) ServerMapRebuildCacheButton.IsEnabled = false;
    }

    private async Task RunServerMapMaintenanceAsync(Func<InstanceProfile, Task> action)
    {
        if (_isMaintainingServerMap || ServerMapProfileComboBox.SelectedItem is not InstanceProfile profile) return;
        _isMaintainingServerMap = true;
        SetServerMapMaintenanceControls(false);
        try { await action(profile); }
        catch (Exception ex) { SetServerMapStatus(T($"操作失败：{ex.Message}", $"Operation failed: {ex.Message}")); }
        finally
        {
            try
            {
                var status = _serverMapService.GetStatus(profile);
                ServerMapToggleButton.Content = status.IsRunning ? T("停止地图", "Stop map") : T("启动地图", "Start map");
                await RefreshServerMapModStatusAsync(profile);
            }
            catch (Exception ex)
            {
                SetServerMapStatus(T($"状态刷新失败：{ex.Message}", $"Status refresh failed: {ex.Message}"));
            }
            finally
            {
                _isMaintainingServerMap = false;
                SetServerMapMaintenanceControls(true);
                await RefreshMapCacheProgressAsync();
            }
        }
    }

    private async Task RefreshServerMapModStatusAsync(InstanceProfile profile)
    {
        try
        {
            var info = await _serverMapService.InspectMapModAsync(profile);
            if (_editingServerMapProfileId != profile.Id) return;
            ServerMapModStatusTextBlock.Text = info.Conflicts.Count > 0
                ? T("检测到模组冲突，请手动处理：", "Mod conflicts; resolve manually: ") + string.Join("; ", info.Conflicts)
                : info.TargetHash is null
                    ? T("未检测到地图模组，请手动部署；网页服务可启动，但地图数据可能不可用。", "Map mod missing: deploy it manually. The web service can start, but map data may be unavailable.")
                    : T($"已安装模组：{info.InstalledVersion}；内置：{info.SourceVersion}。模组仅手动部署，不自动覆盖。",
                        $"Installed mod: {info.InstalledVersion}; bundled: {info.SourceVersion}. Manual deployment only; never overwritten automatically.");
        }
        catch (Exception ex)
        {
            if (_editingServerMapProfileId == profile.Id)
                ServerMapModStatusTextBlock.Text = T($"模组检查失败（未修改文件）：{ex.Message}", $"Mod check failed (no files changed): {ex.Message}");
        }
    }

    private async Task<bool> RequireSavedServerMapSettingsAsync(InstanceProfile profile)
    {
        var saved = await _serverMapService.LoadSettingsAsync(profile);
        var edited = await CollectServerMapSettingsAsync(profile);
        if (JsonSerializer.Serialize(saved) == JsonSerializer.Serialize(edited)) return true;
        SetServerMapStatus(T("配置存在未保存修改，请先保存后再操作。", "Settings have unsaved changes. Save them before continuing."));
        return false;
    }

    private async Task<bool> ConfirmServerMapMaintenanceAsync(string title, string message)
    {
        var cancel = new Button { Content = T("取消", "Cancel"), Classes = { "SecondaryActionButton" } };
        var confirm = new Button { Content = T("确认", "Confirm"), Classes = { "ActionButton" } };
        var dialog = new Window
        {
            Title = title, Width = Math.Clamp(Bounds.Width - 40, 360, 640), Height = 400,
            CanResize = true, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new DockPanel
            {
                Margin = new Thickness(20), LastChildFill = true,
                Children =
                {
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 12, Margin = new Thickness(0, 16, 0, 0), Children = { cancel, confirm },
                        [DockPanel.DockProperty] = Dock.Bottom
                    },
                    new ScrollViewer { Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap } }
                }
            }
        };
        cancel.Click += (_, _) => dialog.Close(false);
        confirm.Click += (_, _) => dialog.Close(true);
        return await dialog.ShowDialog<bool>(this);
    }

    private async void OnServerMapSaveClick(object? sender, RoutedEventArgs e) =>
        await RunServerMapMaintenanceAsync(async profile =>
        {
            var settings = await CollectServerMapSettingsAsync(profile);
            var running = _serverMapService.GetStatus(profile).IsRunning;
            await _serverMapService.SaveSettingsAsync(profile, settings);
            await LoadServerMapForProfileAsync(profile);
            SetServerMapStatus(running
                ? T("配置已保存，重启地图网页服务后生效。", "Settings saved. Restart the map web service to apply them.")
                : T("地图配置已保存。", "Map settings saved."));
        });

    private async void OnServerMapDeployClick(object? sender, RoutedEventArgs e) =>
        await RunServerMapMaintenanceAsync(async profile =>
        {
            var info = await _serverMapService.InspectMapModAsync(profile);
            if (info.Conflicts.Count > 0)
            {
                SetServerMapStatus(T("部署已停止，请先处理模组冲突：\n", "Deployment stopped. Resolve mod conflicts first:\n") + string.Join("\n", info.Conflicts));
                return;
            }
            if (string.IsNullOrEmpty(info.SourceHash))
                throw new InvalidOperationException(T("内置地图模组包不存在，请重新安装 LauncherGo。", "Bundled map mod is missing. Reinstall LauncherGo."));
            if (info.IsIdentical) { SetServerMapStatus(T("模组内容相同，无需替换。", "Mod contents are identical; no replacement needed.")); return; }
            var server = await _serverProcessService.RefreshStatusAsync(profile.Id);
            var message = T($"目标：{info.TargetPath}\n已安装：{(info.TargetHash is null ? "未安装" : info.InstalledVersion)}\n内置版本：{info.SourceVersion}\n\n仅部署当前服务器档案，不修改客户端。已有模组将先备份到 ServerMap/Backups 后替换。",
                $"Target: {info.TargetPath}\nInstalled: {(info.TargetHash is null ? "not installed" : info.InstalledVersion)}\nBundled: {info.SourceVersion}\n\nOnly this server profile is affected, not clients. Existing files will be backed up to ServerMap/Backups before replacement.");
            if (server.IsRunning) message += T("\n\n游戏服务器正在运行：替换文件不会热更新，需下次手动重启游戏服务器生效。不会自动重启。",
                "\n\nThe game server is running. Files are not hot-loaded: a manual game-server restart is required. No automatic restart.");
            if (!await ConfirmServerMapMaintenanceAsync(T("部署地图模组", "Deploy map mod"), message)) return;
            var result = await _serverMapService.DeployMapModAsync(profile, info);
            SetServerMapStatus((result.FilesChanged == 0 ? T("模组无需替换。", "No mod replacement needed.") : T("地图模组已部署，下次游戏服务器启动时加载。", "Map mod deployed; loaded on the next game-server start."))
                + BackupMessage(result));
        });

    private async void OnServerMapResetWebClick(object? sender, RoutedEventArgs e) =>
        await RunServerMapMaintenanceAsync(async profile =>
        {
            if (!await RequireSavedServerMapSettingsAsync(profile)) return;
            var info = await _serverMapService.InspectWebResetAsync(profile);
            var target = info.TargetPath ?? T("默认内置网页（下次启动应用）", "Default bundled page (applies on next start)");
            var message = T($"来源：{info.SourcePath}\n目标：{target}\n\n使用当前软件内置网页，不联网下载。将备份并覆盖同名网页文件（包括直接修改过的 HTML/JS/CSS），保留额外文件。\n\n不修改模组、地图数据、瓦片缓存、公告、自定义 JS/CSS 配置或浏览器设置。",
                $"Source: {info.SourcePath}\nTarget: {target}\n\nUse this installation's bundled web files; no download. Matching files (including edited HTML/JS/CSS) are backed up and replaced; extra files are retained.\n\nMods, map data, tile caches, announcements, custom JS/CSS settings and browser preferences are unchanged.");
            if (info.RequiresHostRestart) message += T("\n\n网页目录已切换，重置后需手动重启地图网页服务。", "\n\nThe web directory changed. Manually restart the map web service after reset.");
            if (!await ConfirmServerMapMaintenanceAsync(T("重置网页", "Reset web files"), message)) return;
            var result = await _serverMapService.ResetWebRootAsync(profile, info);
            var suffix = result.RequiresHostRestart ? T(" 请手动重启地图网页服务以切换目录。", " Manually restart the map web service to switch directories.")
                : result.AppliesOnNextStart || !_serverMapService.GetStatus(profile).IsRunning ? T(" 下次启动地图时应用。", " Applies on the next map start.")
                : T(" 刷新浏览器即可生效。", " Refresh the browser to apply.");
            SetServerMapStatus(T($"网页重置完成，复制 {result.FilesChanged} 个文件。", $"Web reset complete: {result.FilesChanged} files copied.") + suffix + BackupMessage(result));
        });

    private string BackupMessage(ServerMapMaintenanceResult result) => result.BackupPath is null ? ""
        : T($" 备份：{result.BackupPath}", $" Backup: {result.BackupPath}");

    private async void OnServerMapToggleClick(object? sender, RoutedEventArgs e) =>
        await RunServerMapMaintenanceAsync(async profile =>
        {
            var running = _serverMapService.GetStatus(profile).IsRunning;
            if (!running && !await RequireSavedServerMapSettingsAsync(profile)) return;
            SetServerMapStatus(running ? T("停止中…", "Stopping…") : T("启动中…", "Starting…"));
            if (running) await _serverMapService.StopAsync(profile);
            else await _serverMapService.StartAsync(profile);
            var status = _serverMapService.GetStatus(profile);
            SetServerMapStatus(status.IsRunning ? T($"运行中：{status.Url}", $"Running: {status.Url}") : T("未启动", "Stopped"));
        });

    private void OnServerMapOpenClick(object? sender, RoutedEventArgs e)
    {
        if (_isMaintainingServerMap || ServerMapProfileComboBox.SelectedItem is not InstanceProfile profile) return;
        try
        {
            var status = _serverMapService.GetStatus(profile);
            if (!status.IsRunning || string.IsNullOrWhiteSpace(status.Url))
            {
                SetServerMapStatus(T("请先启动地图网页服务。", "Start the map web service first."));
                return;
            }
            Process.Start(new ProcessStartInfo(status.Url) { UseShellExecute = true });
        }
        catch (Exception ex) { SetServerMapStatus(T($"打开地图失败：{ex.Message}", $"Could not open map: {ex.Message}")); }
    }
}
