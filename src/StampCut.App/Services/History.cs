using StampCut.Core.Presets;

namespace StampCut.App.Services;

/// <summary>История изменений проекта на снимках состояния: Undo/Redo и признак «есть несохранённое».</summary>
public sealed class History
{
    private readonly List<string> _undo = new();
    private readonly List<string> _redo = new();
    private string _savedJson = "";
    public const int Limit = 200;

    public bool CanUndo => _undo.Count > 1;
    public bool CanRedo => _redo.Count > 0;
    public string? Current => _undo.Count > 0 ? _undo[^1] : null;
    public bool IsDirty => Current is not null && Current != _savedJson;

    /// <summary>Записать снимок, если состояние изменилось. Возвращает true, если снимок добавлен.</summary>
    public bool Capture(ProjectFile pf)
    {
        var json = ProjectStore.Serialize(pf);
        if (_undo.Count > 0 && _undo[^1] == json) return false;
        _undo.Add(json);
        _redo.Clear();
        if (_undo.Count > Limit) _undo.RemoveAt(0);
        return true;
    }

    public ProjectFile? Undo()
    {
        if (!CanUndo) return null;
        _redo.Add(_undo[^1]);
        _undo.RemoveAt(_undo.Count - 1);
        return ProjectStore.Parse(_undo[^1]);
    }

    public ProjectFile? Redo()
    {
        if (!CanRedo) return null;
        var json = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(json);
        return ProjectStore.Parse(json);
    }

    public void MarkSaved() => _savedJson = Current ?? "";

    /// <summary>Заменить текущий снимок без нового шага (служебные данные вроде замера громкости): признак «сохранено» не меняется.</summary>
    public void Amend(ProjectFile pf)
    {
        if (_undo.Count == 0) { Capture(pf); return; }
        var json = ProjectStore.Serialize(pf);
        if (_undo[^1] == _savedJson) _savedJson = json;
        _undo[^1] = json;
    }

    public void Reset(ProjectFile pf, bool saved)
    {
        _undo.Clear(); _redo.Clear();
        _undo.Add(ProjectStore.Serialize(pf));
        _savedJson = saved ? _undo[0] : "";
    }
}
