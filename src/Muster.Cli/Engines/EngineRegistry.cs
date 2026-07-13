using BattleScribeSpec.Protocol;
using BattleScribeSpec.Roster;
using WarHub.ArmouryModel.RosterEngine.Spec;

namespace Muster.Cli.Engines;

public static class EngineRegistry
{
    public static readonly IReadOnlyList<string> DefaultGoverning = ["newrecruit", "battlescribe", "wham"];

    public static IReadOnlyList<EngineSpec> ParseAll(IReadOnlyList<string> specs) =>
        specs.Count == 0 ? [EngineSpec.Parse(EngineSpec.BuiltinName)] : [.. specs.Select(EngineSpec.Parse)];

    public static bool IsAvailable(EngineSpec spec) => spec.Kind switch
    {
        EngineKind.Builtin => true,
        EngineKind.Docker => CanStart("docker", "--version"),
        EngineKind.Exec when string.Equals(spec.Executable, "dotnet", StringComparison.Ordinal) =>
            spec.Arguments is { } args && File.Exists(FirstToken(args)),
        EngineKind.Exec => File.Exists(spec.Executable) || CanStart(spec.Executable!, "--version"),
        _ => false,
    };

    public static IRosterEngine CreateEngine(EngineSpec spec) => spec.Kind switch
    {
        EngineKind.Builtin => new SpecRosterEngineAdapter(),
        _ => new CompositeDisposableEngine(AdapterProcess.Start(spec.Executable!, spec.Arguments)),
    };

    public static string? ResolveGoverning(IReadOnlyList<string> precedence, IReadOnlyList<string> ranEngines) =>
        precedence.FirstOrDefault(p => ranEngines.Contains(p, StringComparer.Ordinal));

    private static string FirstToken(string s)
    {
        var i = s.IndexOf(' ', StringComparison.Ordinal);
        return i < 0 ? s : s[..i];
    }

    private static bool CanStart(string exe, string args)
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            p?.WaitForExit(5000);
            return p is { ExitCode: 0 };
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Wraps JsonProtocolEngine and AdapterProcess to ensure both are disposed.
    /// JsonProtocolEngine does not dispose the AdapterProcess it owns, so we must do it here.
    /// </summary>
    private sealed class CompositeDisposableEngine : IRosterEngine
    {
        private readonly AdapterProcess _process;
        private readonly JsonProtocolEngine _engine;

        public CompositeDisposableEngine(AdapterProcess process)
        {
            _process = process;
            _engine = new JsonProtocolEngine(process);
        }

        public void SetTestContext(string specId) => _engine.SetTestContext(specId);

        public IReadOnlyList<string> Setup(ProtocolGameSystem gameSystem, ProtocolCatalogue[] catalogues) =>
            _engine.Setup(gameSystem, catalogues);

        public IReadOnlyList<string> SetupFromFiles(IReadOnlyList<(string FileName, string Content)> files) =>
            _engine.SetupFromFiles(files);

        public ActionOutputs AddForce(string forceEntryId, string catalogueId) =>
            _engine.AddForce(forceEntryId, catalogueId);

        public ActionOutputs AddChildForce(string parentForceId, string forceEntryId, string catalogueId) =>
            _engine.AddChildForce(parentForceId, forceEntryId, catalogueId);

        public void RemoveForce(string forceId) => _engine.RemoveForce(forceId);

        public ActionOutputs SelectEntry(string forceId, string entryId) =>
            _engine.SelectEntry(forceId, entryId);

        public ActionOutputs SelectChildEntry(string forceId, string parentSelectionId, string entryId) =>
            _engine.SelectChildEntry(forceId, parentSelectionId, entryId);

        public void DeselectSelection(string forceId, string selectionId) =>
            _engine.DeselectSelection(forceId, selectionId);

        public void SetSelectionCount(string forceId, string selectionId, int count) =>
            _engine.SetSelectionCount(forceId, selectionId, count);

        public ActionOutputs DuplicateSelection(string forceId, string selectionId) =>
            _engine.DuplicateSelection(forceId, selectionId);

        public ActionOutputs DuplicateForce(string forceId) => _engine.DuplicateForce(forceId);

        public void SetCostLimit(string costTypeId, decimal value) =>
            _engine.SetCostLimit(costTypeId, value);

        public void SetCustomization(string forceId, string? selectionId, string? categoryEntryId, string? customName, string? customNotes) =>
            _engine.SetCustomization(forceId, selectionId, categoryEntryId, customName, customNotes);

        public RosterState GetRosterState() => _engine.GetRosterState();

        public IReadOnlyList<ValidationErrorState> GetValidationErrors() =>
            _engine.GetValidationErrors();

        public void Dispose()
        {
            _engine.Dispose();
            _process.Dispose();
        }
    }
}
