using UpdateExpress.Services.Security.Providers;

namespace UpdateExpress.Tests;

/// <summary>
/// A registry held in memory for the security check tests. It notes every value whose data
/// was asked for, so a test can show that a check never opened a value it must not read.
/// </summary>
internal sealed class SecurityTestRegistry : IMachineRegistry
{
    private readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, object>> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _locked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>"key|name" of every value whose data was read.</summary>
    internal List<string> DataReads { get; } = new();

    internal SecurityTestRegistry Key(string key)
    {
        _keys.Add(key);
        return this;
    }

    internal SecurityTestRegistry Value(string key, string name, object data)
    {
        _keys.Add(key);
        if (!_values.TryGetValue(key, out var values))
            _values[key] = values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        values[name] = data;
        return this;
    }

    internal SecurityTestRegistry Locked(string key)
    {
        _locked.Add(key);
        return this;
    }

    public bool KeyExists(string key)
    {
        Open(key);
        return _keys.Contains(key);
    }

    public int? ReadNumber(string key, string name) => Data(key, name) switch
    {
        int number => number,
        string text when int.TryParse(text, out int number) => number,
        _ => null
    };

    public string? ReadText(string key, string name) => Data(key, name) switch
    {
        string text => text,
        int number => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => null
    };

    public IReadOnlyList<string>? ValueNames(string key)
    {
        Open(key);
        if (!_keys.Contains(key)) return null;
        return _values.TryGetValue(key, out var values) ? values.Keys.ToArray() : Array.Empty<string>();
    }

    public int? CountLines(string key, string name) => Data(key, name) switch
    {
        string[] lines => lines.Count(line => !string.IsNullOrWhiteSpace(line)),
        string text => string.IsNullOrWhiteSpace(text) ? 0 : 1,
        null => null,
        _ => 0
    };

    private void Open(string key)
    {
        if (_locked.Contains(key)) throw new System.Security.SecurityException("Requested registry access is not allowed.");
    }

    private object? Data(string key, string name)
    {
        Open(key);
        DataReads.Add(key + "|" + name);
        return _values.TryGetValue(key, out var values) && values.TryGetValue(name, out var data) ? data : null;
    }
}
