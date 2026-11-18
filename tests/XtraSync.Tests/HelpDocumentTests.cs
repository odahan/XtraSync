using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;

namespace XtraSync.Tests;

public sealed class HelpDocumentTests : TestWorkspace
{
    private static byte[] BundledHelp()
    {
        using Stream stream = typeof(HelpDocument).Assembly.GetManifestResourceStream(HelpDocument.ResourceName)!;
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }

    [Fact]
    public void EachClickRecreatesTheExactEmbeddedBytesBeforeOpeningEvenIfUnchanged()
    {
        byte[] expected = BundledHelp();
        string directory = Path.Combine(Root, "output with spaces");
        int opens = 0;
        var help = new HelpDocument(directory, path =>
        {
            Assert.Equal(Path.Combine(directory, HelpDocument.FileName), path);
            Assert.Equal(expected, File.ReadAllBytes(path));
            opens++;
        });
        help.Show();
        File.SetLastWriteTimeUtc(help.FilePath, Baseline);
        help.Show();
        Assert.NotEqual(Baseline, File.GetLastWriteTimeUtc(help.FilePath));
        File.WriteAllText(help.FilePath, "outdated or manually edited");
        help.Show();
        File.Delete(help.FilePath);
        help.Show();
        Assert.Equal(4, opens);
        Assert.Single(Directory.GetFiles(directory));
        Assert.Equal(Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, HelpDocument.FileName), new HelpDocument().FilePath);
    }

    [Fact]
    public void FailureToWriteNeverOpensTheOldFileAndCleansTheTemporary()
    {
        var help = new HelpDocument(Root, _ => throw new Xunit.Sdk.XunitException("Must not open stale help."));
        File.WriteAllText(help.FilePath, "old");
        using (new FileStream(help.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Exception? failure = Record.Exception(help.Show);
            Assert.True(failure is IOException or UnauthorizedAccessException);
        }
        Assert.Equal("old", File.ReadAllText(help.FilePath));
        Assert.Empty(Directory.GetFiles(Root, "*.tmp"));
    }

    [Fact]
    public void HelpRemainsAvailableWhileBusyAndShowsShellErrorsWithoutChangingRunState()
    {
        bool fail = true;
        var help = new HelpDocument(Root, _ => { if (fail) throw new Win32Exception("No HTML association."); });
        var model = Model(help);
        model.IsBusy = true;
        model.Status = "Mirroring folders…";
        Assert.True(model.HelpCommand.CanExecute(null));
        model.HelpCommand.Execute(null);
        Assert.Contains("No HTML association.", model.HelpError);
        Assert.Equal(BundledHelp(), File.ReadAllBytes(help.FilePath));
        Assert.True(model.IsBusy);
        Assert.Equal("Mirroring folders…", model.Status);
        fail = false;
        model.HelpCommand.Execute(null);
        Assert.Empty(model.HelpError);
        Assert.True(model.IsBusy);
    }

    [Fact]
    public void UnwritableHelpDirectoryIsReportedWithoutOpeningABrowser()
    {
        string blocked = Write(Root, "blocked", "file");
        var model = Model(new HelpDocument(blocked, _ => throw new Xunit.Sdk.XunitException("Must not open.")));
        model.HelpCommand.Execute(null);
        Assert.StartsWith("Help could not be opened:", model.HelpError);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HelpIsProtectedInExecutableDirectoryIncludingWhenRefreshedAfterPreview(bool inSource)
    {
        string output = Path.Combine(inSource ? Left : Right, "runtime");
        var help = new HelpDocument(output, _ => { });
        help.Show();
        Write(Left, "copy.txt");
        Write(Right, "delete.txt");
        var settings = new ComparisonSettings(DeleteExcludedFromDestination: true, RunArtifacts: new RunLogStore(output));
        MirrorPlan plan = FolderService.Plan(Left, Right, MirrorMode.Right, settings);
        Assert.DoesNotContain(plan.Actions, action => action.RelativePath.EndsWith(HelpDocument.FileName));
        help.Show();
        FolderService.Execute(plan);
        Assert.Equal(BundledHelp(), File.ReadAllBytes(help.FilePath));
        Assert.True(File.Exists(Path.Combine(Right, "copy.txt")));
        Assert.False(File.Exists(Path.Combine(Right, "delete.txt")));
        if (inSource) Assert.False(File.Exists(Path.Combine(Right, "runtime", HelpDocument.FileName)));
        Assert.False(new RunLogStore(output).IsArtifact(Path.Combine(Root, HelpDocument.FileName)));
    }

    [Fact]
    public void BothCompleteLanguagesAreBundledWithValidNavigationAndNoExternalDependencies()
    {
        string html = Encoding.UTF8.GetString(BundledHelp());
        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains("lang=\"fr\"", html);
        Assert.Contains("lang=\"en\"", html);
        Assert.DoesNotContain("https://", html);
        Assert.DoesNotContain("http://", html);
        Assert.DoesNotContain("<script src=", html);
        Assert.DoesNotContain("<link ", html);
        string[] ids = Regex.Matches(html, "\\bid=\"([^\"]+)\"").Select(match => match.Groups[1].Value).ToArray();
        Assert.Equal(ids.Length, ids.Distinct().Count());
        foreach (Match link in Regex.Matches(html, "href=\"#([^\"]+)\"")) Assert.Contains(link.Groups[1].Value, ids);
        Assert.Equal(ids.Where(id => id.StartsWith("fr-")).Select(id => id[3..]),
            ids.Where(id => id.StartsWith("en-")).Select(id => id[3..]));
        Assert.Equal(24, Regex.Matches(html, "<section ").Count);
    }

    private MainViewModel Model(HelpDocument help) => new(new FolderHistory(Path.Combine(Root, "history.json")),
        new ExclusionList(Path.Combine(Root, "exclusions.json")), new RunLogStore(Root), helpDocument: help);
}
