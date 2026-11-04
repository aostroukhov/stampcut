using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using StampCut.App.Controls;
using StampCut.App.Services;
using StampCut.Core;
using StampCut.Core.Models;
using StampCut.Core.Presets;
using static StampCut.App.Lang;

namespace StampCut.App;

/// <summary>Undo/Redo, признак несохранённых изменений, автосохранение.</summary>
public sealed partial class MainWindow
{
    private readonly History _history = new();
    private readonly Dictionary<string, MediaInfo> _mediaCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _commitTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly DispatcherTimer _autosaveTimer = new() { Interval = TimeSpan.FromSeconds(60) };
    private bool _restoring;

    private ProjectFile Snapshot() => ProjectStore.ToFile(_clips, _overlays.Select(o => o.Settings), _preset.Id);

    /// <summary>Отложенная запись снимка: вызывается после любого изменения; несколько изменений подряд сливаются в один шаг.</summary>
    private void ScheduleCommit()
    {
        if (_restoring) return;
        _commitTimer.Stop();
        _commitTimer.Start();
    }

    private void CommitNow()
    {
        _commitTimer.Stop();
        if (_restoring) return;
        if (_history.Capture(Snapshot())) UpdateDirtyTitle();
    }

    private void InitHistory()
    {
        _commitTimer.Tick += (_, _) => CommitNow();
        _autosaveTimer.Tick += (_, _) => Autosave("timer");
        _autosaveTimer.Start();
        _history.Reset(Snapshot(), saved: true);
    }

    // сначала записать незавершённую правку, и только потом двигаться по истории — иначе она ляжет поверх восстановленного состояния
    private void Undo() { CommitNow(); RestoreFromHistory(_history.Undo()); }
    private void Redo() { CommitNow(); RestoreFromHistory(_history.Redo()); }

    private void RestoreFromHistory(ProjectFile? pf)
    {
        if (pf is null) return;
        _restoring = true;
        try { RestoreSnapshot(pf); }
        finally { _restoring = false; }
        UpdateDirtyTitle();
    }

    /// <summary>Перестроить проект из снимка: медиа из кэша, текущий ролик и позиция — по возможности сохраняются.</summary>
    private void RestoreSnapshot(ProjectFile pf)
    {
        var result = ProjectStore.FromFile(pf, path => _mediaCache.TryGetValue(path, out var m) ? m : null);
        int currentIndex = _current is null ? -1 : _clips.IndexOf(_current);
        string? currentPath = _current?.Media.Path;
        double pos = Position;
        _clips.Clear(); _clips.AddRange(result.Clips);
        _overlays.Clear();
        foreach (var o in result.Overlays) { var item = new OverlayItem { Settings = o }; LoadOverlayImage(item); _overlays.Add(item); }
        _selectedOverlay = _overlays.LastOrDefault()?.Settings;
        _preview.Overlays.Clear(); _preview.Overlays.AddRange(_overlays);
        _preview.SelectedOverlay = _selectedOverlay;
        if (_exportPresets.FirstOrDefault(p => p.Id == result.ExportPresetId) is { } preset && !ReferenceEquals(preset, _preset)) { _preset = preset; RefreshPresetCombos(); _timeline.SnapToKeyframes = _preset.StreamCopy; }
        _activeSegment = null; _selectedRedaction = null;
        RefreshClipList(); SyncOverlayPanel(); UpdateExportInfo();
        if (_clips.Count == 0) { _current = null; ClearPreview(); return; }
        var target = _clips.FirstOrDefault(c => string.Equals(c.Media.Path, currentPath, StringComparison.OrdinalIgnoreCase)) ?? _clips[Math.Clamp(currentIndex, 0, _clips.Count - 1)];
        bool samePath = string.Equals(target.Media.Path, currentPath, StringComparison.OrdinalIgnoreCase);
        SelectClip(target, startAt: samePath ? pos : null, autoplay: false, keepPlayer: samePath);
    }

    private void UpdateDirtyTitle()
    {
        var name = _projectPath is null ? null : Path.GetFileName(_projectPath);
        SetTitle(name, _history.IsDirty);
    }

    /// <summary>Спросить про несохранённые изменения. true — можно продолжать (сохранили или отказались), false — отмена.</summary>
    private bool ConfirmDiscardChanges()
    {
        CommitNow();
        if (!_history.IsDirty || _clips.Count == 0) return true;
        var r = MessageBox.Show(this, T("Сохранить изменения в проекте?"), "StampCut", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.Cancel) return false;
        if (r == MessageBoxResult.Yes) return SaveProject(saveAs: false);
        DeleteAutosave(_projectPath); // пользователь осознанно отказался от изменений — резервную копию не предлагать
        return true;
    }

    // ------------------------------------------------------------------ автосохранение

    private static string AutosaveDir => AppPaths.Ensure(Path.Combine(AppPaths.Local, "autosave"));

    private static string AutosaveKey(string? projectPath)
    {
        if (projectPath is null) return "untitled";
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(projectPath.ToLowerInvariant())))[..12].ToLowerInvariant();
        return Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(projectPath)) + "-" + hash;
    }

    private string AutosavePath(string? projectPath) => Path.Combine(AutosaveDir, AutosaveKey(projectPath) + ProjectFile.Extension);

    /// <summary>Резервная копия проекта: по таймеру и перед рискованными операциями.</summary>
    private void Autosave(string reason)
    {
        try
        {
            CommitNow();
            if (_clips.Count == 0 || !_history.IsDirty) return;
            var path = AutosavePath(_projectPath);
            ProjectStore.Save(path, _clips, _overlays.Select(o => o.Settings), _preset.Id);
            File.WriteAllText(path + ".origin", _projectPath ?? "");
            Logger.Info($"Автосохранение ({reason}): {path}");
        }
        catch (Exception ex) { Logger.Warn("Автосохранение: " + ex.Message); }
    }

    private void DeleteAutosave(string? projectPath)
    {
        try
        {
            var path = AutosavePath(projectPath);
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".origin")) File.Delete(path + ".origin");
        }
        catch { }
    }

    /// <summary>Есть ли резервная копия для проекта (или безымянного), которая новее самого файла.</summary>
    private string? FindAutosave(string? projectPath)
    {
        var path = AutosavePath(projectPath);
        if (!File.Exists(path)) return null;
        if (projectPath is not null && File.Exists(projectPath) && File.GetLastWriteTimeUtc(projectPath) >= File.GetLastWriteTimeUtc(path)) return null;
        return path;
    }

    private async Task OfferAutosaveRestoreAsync(string? projectPath)
    {
        var auto = FindAutosave(projectPath);
        if (auto is null) return;
        var when = File.GetLastWriteTime(auto);
        var r = MessageBox.Show(this, $"{T("Найдена резервная копия проекта от")} {when:g}. {T("Восстановить её?")}", "StampCut", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) { DeleteAutosave(projectPath); return; }
        await OpenProjectAsync(auto, asAutosaveOf: projectPath ?? "");
    }
}
