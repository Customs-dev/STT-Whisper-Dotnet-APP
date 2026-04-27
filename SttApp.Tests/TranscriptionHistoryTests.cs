using System.IO;
using Xunit;
using SttApp;

namespace SttApp.Tests;

public class TranscriptionHistoryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _file;

    public TranscriptionHistoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "Prompto.Tests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _file = Path.Combine(_tempDir, "history.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Add_StoresItem_AndPersists()
    {
        var h = new TranscriptionHistory(_file);

        h.Add("Hello world", 1.5, "en");

        Assert.Single(h.Items);
        Assert.Equal("Hello world", h.Items[0].Text);
        Assert.Equal(1.5, h.Items[0].DurationSeconds);
        Assert.Equal("en", h.Items[0].Language);
        Assert.True(File.Exists(_file));

        // Reload from disk
        var h2 = new TranscriptionHistory(_file);
        Assert.Single(h2.Items);
        Assert.Equal("Hello world", h2.Items[0].Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Add_IgnoresWhitespaceOnlyText(string input)
    {
        var h = new TranscriptionHistory(_file);

        h.Add(input, 1.0, "cs");

        Assert.Empty(h.Items);
    }

    [Fact]
    public void Add_NewestFirst()
    {
        var h = new TranscriptionHistory(_file);

        h.Add("first", 0.1, "cs");
        h.Add("second", 0.2, "cs");
        h.Add("third", 0.3, "cs");

        Assert.Equal(new[] { "third", "second", "first" },
                     h.Items.Select(i => i.Text).ToArray());
    }

    [Fact]
    public void Add_RespectsMaxItemsLimit()
    {
        var h = new TranscriptionHistory(_file, maxItems: 3);

        for (int i = 0; i < 10; i++)
            h.Add($"text-{i}", i, "cs");

        Assert.Equal(3, h.Items.Count);
        // newest 3: text-9, text-8, text-7
        Assert.Equal("text-9", h.Items[0].Text);
        Assert.Equal("text-8", h.Items[1].Text);
        Assert.Equal("text-7", h.Items[2].Text);
    }

    [Fact]
    public void Remove_RemovesItem_AndPersists()
    {
        var h = new TranscriptionHistory(_file);
        h.Add("a", 1.0, "cs");
        h.Add("b", 2.0, "cs");

        var toRemove = h.Items.First(i => i.Text == "a");
        h.Remove(toRemove);

        Assert.Single(h.Items);
        Assert.Equal("b", h.Items[0].Text);

        var h2 = new TranscriptionHistory(_file);
        Assert.Single(h2.Items);
        Assert.Equal("b", h2.Items[0].Text);
    }

    [Fact]
    public void Clear_EmptiesAndPersists()
    {
        var h = new TranscriptionHistory(_file);
        h.Add("a", 1.0, "cs");
        h.Add("b", 2.0, "cs");

        h.Clear();

        Assert.Empty(h.Items);

        var h2 = new TranscriptionHistory(_file);
        Assert.Empty(h2.Items);
    }

    [Fact]
    public void Load_CorruptedJson_DoesNotThrow_AndStartsEmpty()
    {
        File.WriteAllText(_file, "{ this is not valid json [[[");

        var h = new TranscriptionHistory(_file);

        Assert.Empty(h.Items);
    }

    [Fact]
    public void Load_NonExistentFile_StartsEmpty()
    {
        var h = new TranscriptionHistory(Path.Combine(_tempDir, "missing.json"));

        Assert.Empty(h.Items);
    }
}
