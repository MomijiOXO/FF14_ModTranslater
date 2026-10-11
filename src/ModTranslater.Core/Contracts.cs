namespace ModTranslater.Core;

public sealed record InstalledMod(string Root, string Directory, string Name)
{
    public string FullPath => Path.GetFullPath(Path.Combine(Root, Directory));
    public string Key => FullPath;
}
public interface IPenumbraBridge
{
    Task<IReadOnlyList<InstalledMod>> ListAsync(CancellationToken token);
    Task ReloadAsync(InstalledMod mod, CancellationToken token);
}
public interface ITranslationService
{
    Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, CancellationToken token);
}
public sealed record ModResult(string Key, string Name, string Status);
public sealed record JobProgress(int Completed, int Total, string Mod, string Status);
public sealed class ProcessRecord
{
    public string Fingerprint { get; set; } = "";
    public string Status { get; set; } = "";
    public string? Backup { get; set; }
}
