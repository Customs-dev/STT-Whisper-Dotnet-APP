using System.Text.Json;

namespace SttApp;

public sealed class TranscriptionHistoryItem
{
    public DateTime Timestamp { get; set; }
    public string Text { get; set; } = "";
    public double DurationSeconds { get; set; }
    public string Language { get; set; } = "";
}

public sealed class TranscriptionHistory
{
    private readonly List<TranscriptionHistoryItem> _items = new();
    private readonly int _maxItems;
    private readonly string _filePath;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public IReadOnlyList<TranscriptionHistoryItem> Items => _items;

    public TranscriptionHistory(int maxItems = 50)
    {
        _maxItems = maxItems;
        _filePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Prompto", "history.json");
        Load();
    }

    public void Add(string text, double durationSeconds, string language)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        _items.Insert(0, new TranscriptionHistoryItem
        {
            Timestamp = DateTime.Now,
            Text = text,
            DurationSeconds = durationSeconds,
            Language = language,
        });

        if (_items.Count > _maxItems)
            _items.RemoveRange(_maxItems, _items.Count - _maxItems);

        Save();
    }

    public void Remove(TranscriptionHistoryItem item)
    {
        _items.Remove(item);
        Save();
    }

    public void Clear()
    {
        _items.Clear();
        Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var json = File.ReadAllText(_filePath);
            var items = JsonSerializer.Deserialize<List<TranscriptionHistoryItem>>(json);
            if (items is not null)
            {
                _items.AddRange(items.Take(_maxItems));
            }
        }
        catch
        {
            // Poškozený soubor – ignoruj
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(_items, JsonOpts));
        }
        catch
        {
            // Nelze zapsat – ignoruj
        }
    }
}
