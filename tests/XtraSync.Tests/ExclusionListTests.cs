namespace XtraSync.Tests;

public sealed class ExclusionListTests : TestWorkspace
{
    [Theory]
    [InlineData("", "file.tmp", false)]
    [InlineData(" ; \r\n / ", "file.tmp", false)]
    [InlineData("*.tmp", "deep/FILE.TMP", true)]
    [InlineData("*.tmp", "file.tmp.bak", false)]
    [InlineData("file?.txt", "file1.txt", true)]
    [InlineData("file?.txt", "file12.txt", false)]
    [InlineData("file?.txt", "file.txt", false)]
    [InlineData("cache/*.log", "cache/a.log", true)]
    [InlineData("cache/*.log", "cache/deep/a.log", false)]
    [InlineData("cache/*.log", "other/cache/a.log", false)]
    [InlineData("cache\\*.log", "CACHE\\a.log", true)]
    [InlineData("  *.tmp ;\r\n .git  ", "deep/.git", true)]
    [InlineData("a b,c.txt", "a b,c.txt", true)]
    [InlineData("a b,c.txt", "b", false)]
    [InlineData("[ab].txt", "a.txt", false)]
    [InlineData("[ab].txt", "[ab].txt", true)]
    [InlineData("/cache/*.log/", "cache/.log", true)]
    public void PatternsHonorNamesPathsWildcardsAndLiteralCharacters(string pattern, string path, bool expected) =>
        Assert.Equal(expected, ExclusionList.Matcher(pattern)(path));

    [Fact]
    public void SavesLoadsOverwritesAndClears()
    {
        string path = Path.Combine(Root, "nested", "exclusions.json");
        var store = new ExclusionList(path);
        Assert.Equal("", store.Load());
        store.Save("*.tmp; café");
        Assert.Equal("*.tmp; café", new ExclusionList(path).Load());
        store.Save(".git");
        Assert.Equal(".git", store.Load());
        store.Save("");
        Assert.Equal("", store.Load());
        Assert.Equal([path], Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{invalid")]
    [InlineData("{}")]
    [InlineData("42")]
    public void InvalidOrNullSettingsFallBackToEmpty(string json)
    {
        string path = Write(Root, "settings.json", json);
        Assert.Equal("", new ExclusionList(path).Load());
    }

    [Fact]
    public void UnreadableSettingsFallBackToEmpty() => Assert.Equal("", new ExclusionList(Root).Load());

    [Fact]
    public void FailedReplacementCleansTemporaryFile()
    {
        var store = new ExclusionList(Left);
        Assert.Throws<UnauthorizedAccessException>(() => store.Save("*.tmp"));
        Assert.Empty(Directory.GetFiles(Root, "*.tmp"));
    }
}
