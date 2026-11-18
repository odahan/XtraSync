namespace XtraSync.Tests;

/// <summary>Injects faults at real filesystem boundaries while retaining actual fixture behavior elsewhere.</summary>
internal sealed class FaultFileSystem : FileSystemAccess
{
    public Func<string, int, Exception?>? AttributeFault { get; set; }
    public Func<string, int, Exception?>? EnumerationFault { get; set; }
    public Func<string, int, Exception?>? LengthFault { get; set; }
    public Func<string, int, Exception?>? OpenFault { get; set; }
    public Func<string, int, Exception?>? CreateFault { get; set; }
    public Func<string, int, Exception?>? DeleteFileFault { get; set; }
    public Func<string, int, Exception?>? DeleteFolderFault { get; set; }
    public Func<string, int, Exception?>? MoveFault { get; set; }
    public Func<string, int, Exception?>? ProbeFault { get; set; }
    public Func<string, IEnumerable<string>>? Enumeration { get; set; }
    public Func<string, Stream>? InputFactory { get; set; }
    public Func<string, Stream>? OutputFactory { get; set; }
    private readonly Dictionary<string, int> calls = new(StringComparer.OrdinalIgnoreCase);
    public int Count(string operation, string path) => calls.GetValueOrDefault(operation + "|" + path);
    public static IOException Io(int code) => new("Injected filesystem error " + code, unchecked((int)0x80070000) | code);

    private void Check(string operation, string path, Func<string, int, Exception?>? fault)
    {
        string key = operation + "|" + path;
        int count = calls.GetValueOrDefault(key) + 1;
        calls[key] = count;
        if (fault?.Invoke(path, count) is { } exception) throw exception;
    }
    public override FileAttributes Attributes(string path) { Check("attributes", path, AttributeFault); return base.Attributes(path); }
    public override IEnumerable<string> Enumerate(string path) { Check("enumerate", path, EnumerationFault); return Enumeration?.Invoke(path) ?? base.Enumerate(path); }
    public override long Length(string path) { Check("length", path, LengthFault); return base.Length(path); }
    public override Stream OpenRead(string path) { Check("open", path, OpenFault); return InputFactory?.Invoke(path) ?? base.OpenRead(path); }
    public override Stream CreateNew(string path) => OutputFactory?.Invoke(path) ?? base.CreateNew(path);
    public override void CreateDirectory(string path) { Check("create", path, CreateFault); base.CreateDirectory(path); }
    public override void DeleteFile(string path) { Check("delete-file", path, DeleteFileFault); base.DeleteFile(path); }
    public override void DeleteDirectory(string path) { Check("delete-folder", path, DeleteFolderFault); base.DeleteDirectory(path); }
    public override void Move(string source, string destination) { Check("move", destination, MoveFault); base.Move(source, destination); }
    public override void ProbeRoot(string path) { Check("probe", path, ProbeFault); base.ProbeRoot(path); }

    public ErrorPolicy Policy(int maximumReadErrorFiles = 10) => new(this, new ErrorPolicyOptions
    {
        FileRetryDelays = [TimeSpan.Zero, TimeSpan.Zero],
        RootRecoveryTimeout = TimeSpan.Zero,
        MaximumReadErrorFiles = maximumReadErrorFiles
    }, wait: (_, control) => control?.Checkpoint());
}
