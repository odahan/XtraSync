using System.Diagnostics;

namespace XtraSync.Tests;

public sealed class LinkedFolderTests : TestWorkspace
{
    /// <summary>Creates a Windows junction without requiring symbolic-link privileges.</summary>
    private static void Junction(string path, string target)
    {
        var start = new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/d /c mklink /J \"{path}\" \"{target}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(5000), "Junction creation timed out.");
        Assert.True(process.ExitCode == 0, output + error);
    }

    [Fact]
    public void LinkedRootsAndLinkedAncestorsAreRejected()
    {
        string junction = Path.Combine(Root, "link");
        Directory.CreateDirectory(Path.Combine(Left, "child"));
        Junction(junction, Left);
        Assert.ThrowsAny<IOException>(() => FolderService.ValidateRoots(junction, Right));
        Assert.ThrowsAny<IOException>(() => FolderService.ValidateRoots(Path.Combine(junction, "child"), Right));
        Assert.ThrowsAny<IOException>(() => FolderService.ValidateRoots(Right, junction));
        Directory.Delete(junction);
        Assert.True(Directory.Exists(Left));
    }

    [Fact]
    public void ScanRejectsJunctionsUnlessExcludedOrOutsideShallowScope()
    {
        string junction = Path.Combine(Left, "link");
        Junction(junction, Right);
        Assert.ThrowsAny<IOException>(() => FolderService.Scan(Left));
        Assert.Empty(FolderService.Scan(Left, exclusions: "link"));
        Assert.Empty(FolderService.Scan(Left, includeSubfolders: false));
        Directory.Delete(junction);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LinkedItemAppearingAfterValidationStopsExecution(bool sourceLinked)
    {
        Write(Left, "file", "new", 10);
        Write(Right, "file", "original");
        var plan = FolderService.Plan(Left, Right, MirrorMode.Right, new());
        string replaced = Path.Combine(sourceLinked ? Left : Right, "file");
        string external = Directory.CreateDirectory(Path.Combine(Root, "external")).FullName;
        Write(external, "untouched", "keep");
        using var control = new OperationControl(update =>
        {
            if (update.Phase != "Mirroring" || update.Path.Length != 0) return;
            File.Delete(replaced);
            Junction(replaced, external);
        });
        try
        {
            Assert.Contains("linked", Assert.ThrowsAny<IOException>(() => FolderService.Execute(plan, control)).Message);
            Assert.Equal("keep", File.ReadAllText(Path.Combine(external, "untouched")));
            if (sourceLinked) Assert.Equal("original", File.ReadAllText(Path.Combine(Right, "file")));
        }
        finally
        {
            if (Directory.Exists(replaced)) Directory.Delete(replaced);
        }
    }

    [Fact]
    public void ExcludedSourceFolderIsNotTraversedEvenWhenItContainsAJunction()
    {
        Directory.CreateDirectory(Path.Combine(Left, "hidden"));
        string junction = Path.Combine(Left, "hidden", "link");
        Junction(junction, Right);
        try
        {
            var plan = FolderService.Plan(Left, Right, MirrorMode.Right,
                new(FolderExclusions: "hidden", DeleteExcludedFromDestination: true));
            Assert.Empty(plan.SourceSnapshot);
            Assert.Empty(plan.Actions);
            FolderService.Execute(plan);
        }
        finally
        {
            Directory.Delete(junction);
        }
    }

    [Fact]
    public void DestinationCleanupStillRejectsExcludedJunctions()
    {
        string junction = Path.Combine(Right, "hidden");
        Junction(junction, Left);
        try
        {
            Assert.Empty(FolderService.Plan(Left, Right, MirrorMode.Right, new(FolderExclusions: "hidden")).Actions);
            Assert.ThrowsAny<IOException>(() => FolderService.Plan(Left, Right, MirrorMode.Right,
                new(FolderExclusions: "hidden", DeleteExcludedFromDestination: true)));
        }
        finally
        {
            Directory.Delete(junction);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LinkedParentAppearingAfterValidationStopsExecution(bool sourceLinked)
    {
        Write(Left, "nested/file", "new", 10);
        Write(Right, "nested/file", "original");
        var plan = FolderService.Plan(Left, Right, MirrorMode.Right, new());
        string replaced = Path.Combine(sourceLinked ? Left : Right, "nested");
        string moved = Path.Combine(Root, "moved");
        using var control = new OperationControl(update =>
        {
            if (update.Phase != "Mirroring" || update.Path.Length != 0) return;
            Directory.Move(replaced, moved);
            Junction(replaced, moved);
        });
        try
        {
            Assert.Contains("Linked folders", Assert.ThrowsAny<IOException>(() => FolderService.Execute(plan, control)).Message);
            Assert.Equal(sourceLinked ? "new" : "original", File.ReadAllText(Path.Combine(moved, "file")));
        }
        finally
        {
            if (Directory.Exists(replaced)) Directory.Delete(replaced);
        }
    }
}
