using System.Text.Json;

namespace XtraSync.Tests;

public sealed class FolderHistoryTests : TestWorkspace
{
    private FolderHistory Store => new(Path.Combine(Root, "settings", "history.json"));

    [Fact]
    public void RemembersSeparateSidesNormalizesAndDeduplicates()
    {
        var store = Store;
        Assert.True(store.Remember(" " + Left + "\\ ", true));
        Assert.False(store.Remember(Left.ToUpperInvariant(), true));
        Assert.True(store.Remember(Right, true));
        Assert.True(store.Remember(Left, true));
        Assert.True(store.Remember(Right, false));
        Assert.Equal([Left, Right], store.Left);
        Assert.Equal([Right], store.Right);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\u0000")]
    [InlineData("?:\\invalid")]
    public void RejectsInvalidPaths(string path) => Assert.False(Store.Remember(path, true));

    [Fact]
    public void RejectsMissingFoldersAndFiles()
    {
        Assert.False(Store.Remember(Path.Combine(Root, "missing"), true));
        Assert.False(Store.Remember(Write(Left, "file"), false));
    }

    [Fact]
    public void KeepsTenMostRecentFolders()
    {
        var store = Store;
        for (int index = 0; index < 12; index++)
        {
            string folder = Directory.CreateDirectory(Path.Combine(Root, index.ToString())).FullName;
            Assert.True(store.Remember(folder, true));
        }
        Assert.Equal(10, store.Left.Count);
        Assert.Equal(Path.Combine(Root, "11"), store.Left[0]);
        Assert.Equal(Path.Combine(Root, "2"), store.Left[^1]);
    }

    [Fact]
    public void SavesLoadsAndAtomicallyReplacesHistory()
    {
        var store = Store;
        store.Remember(Left, true);
        store.Remember(Right, false);
        store.Save();
        var restored = Store;
        restored.Load();
        Assert.Equal([Left], restored.Left);
        Assert.Equal([Right], restored.Right);
        store.Left.Clear();
        store.Save();
        restored.Load();
        Assert.Empty(restored.Left);
        Assert.Equal([Right], restored.Right);
        Assert.Single(Directory.GetFiles(Path.Combine(Root, "settings")));
    }

    [Theory]
    [InlineData("{invalid")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"Left\":null,\"Right\":42}")]
    public void DamagedOrUnexpectedJsonRestoresEmptyLists(string json)
    {
        var store = new FolderHistory(Write(Root, "history.json", json));
        store.Load();
        Assert.Empty(store.Left);
        Assert.Empty(store.Right);
    }

    [Fact]
    public void LoadFiltersInvalidEntriesDeduplicatesAndLimitsCount()
    {
        object?[] values = [Left, Left.ToUpperInvariant() + "\\", " ", "\0", null, 123,
            .. Enumerable.Range(0, 12).Select(index => (object)Path.Combine(Root, index.ToString()))];
        var store = new FolderHistory(Write(Root, "history.json", JsonSerializer.Serialize(new { Left = values })));
        store.Load();
        Assert.Equal(10, store.Left.Count);
        Assert.Equal(Left, store.Left[0]);
        Assert.Empty(store.Right);
    }

    [Fact]
    public void MissingOrUnreadableFileResetsExistingLists()
    {
        foreach (string path in new[] { Root, Path.Combine(Root, "missing.json") })
        {
            var store = new FolderHistory(path) { Left = [Left], Right = [Right] };
            store.Load();
            Assert.Empty(store.Left);
            Assert.Empty(store.Right);
        }
    }

    [Fact]
    public void FailedReplacementCleansTemporaryFile()
    {
        Assert.Throws<UnauthorizedAccessException>(() => new FolderHistory(Left).Save());
        Assert.Empty(Directory.GetFiles(Root, "*.tmp"));
    }
}
