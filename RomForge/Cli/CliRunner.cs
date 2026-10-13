using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Common;
using Common.WPF.ViewModels;
using LibHac.Ns;
using NSW.Core;
using NSW.Core.Enums;
using NSW.HacPack.Models;
using NSW.WPF.Services;
using NSW.WPF.UI;
using NSW.WPF.ViewModels;
using RomForge.Controls.Switch;
using RomForge.Core;
using RomForge.Core.Models;
using RomForge.Core.Models.Compression;
using RomForge.Core.UI.Command;
using RomForge.ViewModels.Patch;
using RomForge.ViewModels.Util;

namespace RomForge.Cli;

internal sealed class CliJob
{
    public string Tool { get; set; } = "";
    public List<string> Inputs { get; set; } = [];
    public Dictionary<string, JsonElement> Set { get; set; } = [];
    public Dictionary<string, JsonElement> Config { get; set; } = [];
    public List<CliStep> Steps { get; set; } = [];
    public string? Format { get; set; }
    public string? SourceEntry { get; set; }
}

internal sealed class CliStep
{
    public string? Action { get; set; }
    public JsonElement[] Args { get; set; } = [];
    public Dictionary<string, JsonElement> Set { get; set; } = [];
}

internal sealed class CliRunner
{
    private bool _json;
    private bool _yes;
    private bool _failed;
    private bool _cancelled;
    private bool _driveSpecified;
    private object? _model;
    private readonly ObservableCollection<GameFile> _inputs = [];
    private readonly HashSet<LogEntry> _seenLogs = [];
    private readonly HashSet<object> _subscribed = new(ReferenceEqualityComparer.Instance);
    private readonly CancellationTokenSource _cancellation = new();

    public static async Task<int> RunAsync(string[] args) => await new CliRunner().ExecuteAsync(args);

    private async Task<int> ExecuteAsync(string[] args)
    {
        _json = args.Contains("--json");
        _yes = args.Contains("--yes");
        try
        {
            if (args[0] is "help" or "--help" or "-h" || args.Contains("--help"))
            {
                string? tool = args[0] == "help" ? args.Skip(1).FirstOrDefault(arg => !arg.StartsWith('-'))
                    : args[0].StartsWith('-') ? null : args[0];
                Help(tool);
                return 0;
            }
            if (args[0] is "--version" or "version")
            {
                Console.WriteLine(typeof(App).Assembly.GetName().Version);
                return 0;
            }
            if (args[0] == "tools")
            {
                if (_json) WriteJson(new { tools = CliCatalog.Tools.Select(CliCatalog.Schema) });
                else foreach (var tool in CliCatalog.Tools) Console.WriteLine($"{tool.Name,-18} {tool.Description}");
                return 0;
            }

            AppConfig.PersistChanges = false;
            ProcessExtensions.SuppressOpening = true;
            if (args[0] == "config") return Configure(args);

            var job = Parse(args);
            var toolDefinition = CliCatalog.Find(job.Tool);
            foreach (var (path, value) in job.Config)
                CliValues.Set(AppConfig.Instance, path, value, ResolveReference);
            AppConfig.Instance.Patch.OpenOutputFolderWhenCompleted = false;

            MessageBoxHelper.NotificationHandler = (level, message) =>
            {
                if (level == "error") _failed = true;
                Emit(new { type = "notification", level, message }, message);
            };
            MessageBoxHelper.QuestionHandler = message => _yes
                ? true
                : throw new InvalidOperationException($"Confirmation required: {message} Pass --yes to approve this operation.");

            _model = Activator.CreateInstance(toolDefinition.Type) ?? throw new InvalidOperationException("Cannot initialize tool.");
            Subscribe(_model);
            Console.CancelKeyPress += OnCancel;
            Application.Current.DispatcherUnhandledException += OnDispatcherException;

            foreach (string input in job.Inputs)
                if (job.Tool != "tistory" && !File.Exists(input) && !Directory.Exists(input))
                    throw new ArgumentException($"Input does not exist: {input}");

            await AddInputsAsync(job);
            foreach (var (path, value) in job.Set)
            {
                await WaitForPreparationAsync();
                SetToolValue(path, value);
            }
            await WaitForPreparationAsync();
            if (job.Format is not null) ApplyFormat(job.Format);

            if (_model is NormalPatchMainViewModel normal)
            {
                normal.RequestSourceSelectionAsync = candidates => Task.FromResult<string?>(
                    candidates.FirstOrDefault(candidate => candidate.EntryKey.Equals(job.SourceEntry, StringComparison.OrdinalIgnoreCase))?.EntryKey
                    ?? throw new InvalidOperationException("Source archive has multiple candidates. Pass --source-entry with an entry path."));
            }

            if (job.Steps.Count == 0)
                job.Steps.Add(new CliStep { Action = toolDefinition.DefaultAction });

            foreach (var step in job.Steps)
            {
                _cancellation.Token.ThrowIfCancellationRequested();
                foreach (var (path, value) in step.Set)
                {
                    await WaitForPreparationAsync();
                    SetToolValue(path, value);
                }
                await WaitForPreparationAsync();
                if (step.Action == "inspect") continue;
                if (step.Action == "export")
                {
                    if (step.Args.Length != 2) throw new ArgumentException("export requires [propertyPath, outputFile].");
                    object? value = ResolveReference(step.Args[0].GetString()!);
                    string path = step.Args[1].GetString()!;
                    if (value is byte[] bytes) await File.WriteAllBytesAsync(path, bytes);
                    else await File.WriteAllTextAsync(path, JsonSerializer.Serialize(CliValues.Snapshot(value)));
                    Emit(new { type = "export", path }, $"Exported: {path}");
                    continue;
                }
                if (_model is NormalPatchMainViewModel patch && (string.IsNullOrWhiteSpace(patch.SourcePath) || string.IsNullOrWhiteSpace(patch.PatchPath)))
                    throw new ArgumentException("patch requires --source and --patch.");
                if (_model is ArcadePatchMainViewModel arcade && !arcade.MatchItems.Any(item => item.IsMatched))
                    throw new InvalidOperationException("No arcade patches matched. Inspect the source and patch inputs first.");
                if (_model is TistoryMainViewModel tistory && step.Action == "RunCommand" && tistory.FileItems.Count == 0)
                    await InvokeAsync("AnalyzeCommand", []);
                await InvokeAsync(step.Action ?? toolDefinition.DefaultAction, step.Args);
                if (_failed) break;
            }

            await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            Emit(new { type = "result", tool = job.Tool, success = !_failed && !_cancelled,
                state = CliValues.Snapshot(_model), inputs = CliValues.Snapshot(_inputs) }, "Completed.");
            return _cancelled ? 130 : _failed ? 1 : 0;
        }
        catch (Exception ex)
        {
            while (ex is TargetInvocationException { InnerException: not null }) ex = ex.InnerException;
            int code = ex is ArgumentException or JsonException ? 2 : ex is OperationCanceledException ? 130 : 1;
            if (_json) WriteJson(new { type = "error", success = false, message = ex.Message, exitCode = code });
            else Console.Error.WriteLine($"Error: {ex.Message}");
            return code;
        }
        finally
        {
            Console.CancelKeyPress -= OnCancel;
            Application.Current.DispatcherUnhandledException -= OnDispatcherException;
            MessageBoxHelper.NotificationHandler = null;
            MessageBoxHelper.QuestionHandler = null;
        }
    }

    private CliJob Parse(string[] args)
    {
        var job = new CliJob { Tool = args[0] };
        CliStep? action = null;
        for (int index = 1; index < args.Length; index++)
        {
            string option = args[index];
            if (option is "--json" or "--yes") continue;
            string value = ++index < args.Length ? args[index] : throw new ArgumentException($"Missing value for {option}.");
            switch (option)
            {
                case "--job":
                    job = JsonSerializer.Deserialize<CliJob>(File.ReadAllText(value), CliValues.JsonOptions)
                        ?? throw new ArgumentException("Job file is empty.");
                    break;
                case "--input": job.Inputs.Add(value); break;
                case "--source": job.Set["SourcePath"] = CliValues.Parse(value); break;
                case "--patch": job.Set["PatchPath"] = CliValues.Parse(value); break;
                case "--source-entry": job.SourceEntry = value; break;
                case "--format": job.Format = value; break;
                case "--set": AddAssignment(job.Set, value); break;
                case "--config": AddAssignment(job.Config, value); break;
                case "--action":
                    action = new CliStep { Action = value };
                    job.Steps.Add(action);
                    break;
                case "--args":
                    if (action is null) throw new ArgumentException("--args requires --action first.");
                    action.Args = JsonSerializer.Deserialize<JsonElement[]>(value) ?? [];
                    break;
                default: throw new ArgumentException($"Unknown option '{option}'. Use --help.");
            }
        }
        return job;
    }

    private static void AddAssignment(Dictionary<string, JsonElement> values, string assignment)
    {
        int split = assignment.IndexOf('=');
        if (split <= 0) throw new ArgumentException("Expected PropertyPath=value.");
        values[assignment[..split]] = CliValues.Parse(assignment[(split + 1)..]);
    }

    private async Task AddInputsAsync(CliJob job)
    {
        if (job.Inputs.Count == 0) return;
        if (job.Tool is "switch.repack" or "switch.merge" or "switch.keygen")
        {
            var sync = new GameFilePatchSyncManager(_inputs);
            var importer = new SwitchGameFileImporter(_inputs, sync, new HashSet<string>([".nsp", ".nsz", ".xci", ".xcz"], StringComparer.OrdinalIgnoreCase));
            await importer.AddFilesAsync(Utils.ExpandPaths(job.Inputs), () => { });
            if (_model is ViewModels.Switch.RepackMainViewModel repack)
            {
                GameMetadata? metadata = KeySetProvider.Instance.KeySet is { } keys
                    ? MetadataService.GetGameMetadata(keys, _inputs.Select(file => file.FilePath).ToList()) : null;
                repack.Context = new(_inputs, metadata, ApplicationControlProperty.Language.None, null, null);
            }
            return;
        }
        if (_model is TistoryMainViewModel tistory) { tistory.PageUrls = string.Join(Environment.NewLine, job.Inputs); return; }
        if (_model is CertsMainViewModel certs) { certs.SetFile(job.Inputs.Single()); return; }
        if (_model is ViewModels._3DS.RepackMainViewModel ds) { ds.InputPath = job.Inputs.Single(); return; }
        if (_model is ViewModels.WiiU.RepackMainViewModel wiiu)
        {
            foreach (string input in job.Inputs) await wiiu.AddDroppedItemAsync(input);
            return;
        }
        if (_model is ViewModels._3DS.InstallerMainViewModel installer) { await installer.Install.AddFiles(job.Inputs); return; }
        if (_model is ViewModels.Switch.ConvertSaturnMainViewModel saturn) { saturn.CuePath = job.Inputs.Single(); return; }
        if (_model is ZipImageToolMainViewModel disk) { disk.WriteImagePath = job.Inputs.Single(); return; }

        var method = CliCatalog.Methods(_model!.GetType()).FirstOrDefault(method => method.Name is "AddPathsAsync" or "AddPaths");
        if (method is null) throw new ArgumentException("This tool uses --set or --source for inputs. See its help.");
        if (method.Invoke(_model, [job.Inputs]) is Task task) await task;
        if (CliValues.Get(_model, "FileItems") is IList { Count: 0 })
            throw new ArgumentException("No supported inputs were registered.");
    }

    private async Task WaitForPreparationAsync()
    {
        if (_model is ArcadePatchMainViewModel arcade) await arcade.WaitForAnalysisAsync();
        if (_model is ViewModels._3DS.RepackMainViewModel ds) await ds.WaitForPreparationAsync();
        if (_model is CertsMainViewModel certs) await certs.WaitForPreparationAsync();
        if (_model is ViewModels.PS.PackingMainViewModel ps) await ps.WaitForPreparationAsync();
    }

    private void SetToolValue(string path, JsonElement value)
    {
        if (path.Equals("SelectedDrive", StringComparison.OrdinalIgnoreCase)) _driveSpecified = true;
        if (path.StartsWith("inputs[", StringComparison.OrdinalIgnoreCase))
            CliValues.Set(new { inputs = _inputs }, path, value, ResolveReference);
        else
            CliValues.Set(_model!, path, value, ResolveReference);
    }

    private void ApplyFormat(string format)
    {
        var files = CliValues.Get(_model!, "FileItems") as IEnumerable
            ?? throw new ArgumentException("This tool uses --set OutputFormat instead of --format.");
        foreach (object item in files)
        {
            if (item is Common.WPF.ViewModels.IConvertible convertible)
            {
                string target = convertible.AvailableFormats.FirstOrDefault(value => value.Equals(format, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"Unsupported format '{format}'. Available: {string.Join(", ", convertible.AvailableFormats)}");
                convertible.SelectedTargetFormat = target;
            }
            else if (item is CompressFileItem compressed)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (!compressed.OutputExtension.Equals(format, StringComparison.OrdinalIgnoreCase))
                    if (!seen.Add(compressed.OutputExtension) || !compressed.ToggleOutputExtension())
                        throw new ArgumentException($"Unsupported output format '{format}' for {compressed.FileName}.");
            }
        }
    }

    private async Task InvokeAsync(string action, JsonElement[] arguments)
    {
        string name = action;
        object target = _model!;
        int split = action.LastIndexOf('.');
        if (split >= 0)
        {
            target = CliValues.Get(target, action[..split]) ?? throw new ArgumentException($"Action owner '{action}' is null.");
            name = action[(split + 1)..];
        }
        if (target is ZipImageToolMainViewModel disk && name is "WriteAsync" or "ReadAsync" or "WriteCommand" or "ReadCommand")
        {
            bool write = name.StartsWith("Write");
            if (!_driveSpecified || disk.SelectedDrive is null || string.IsNullOrWhiteSpace(write ? disk.WriteImagePath : disk.ReadImagePath))
                throw new ArgumentException("Disk operations require an explicit --set SelectedDrive reference and WriteImagePath/ReadImagePath. Inspect UsbDrives first.");
        }
        var property = CliCatalog.Properties(target.GetType()).FirstOrDefault(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (property?.GetValue(target) is ICommand command)
        {
            if (name.StartsWith("Browse") || name.StartsWith("Open") || name.Contains("Settings"))
                throw new ArgumentException("File dialogs/navigation are unavailable in CLI. Set the path property instead.");
            object? argument = arguments.Length > 0 ? CliValues.Convert(arguments[0], typeof(object), ResolveReference) : null;
            if (!command.CanExecute(argument)) throw new InvalidOperationException($"Action '{action}' cannot run with the current inputs.");
            if (command is RelayCommand relay) await relay.ExecuteAsync(argument);
            else if (command is ToggleCommand toggle) await toggle.ExecuteAsync(argument);
            else throw new InvalidOperationException($"Action '{action}' has no awaitable CLI implementation.");
            return;
        }
        var method = CliCatalog.Methods(target.GetType()).FirstOrDefault(method => method.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Unknown action '{action}'. See help for this tool.");
        var parameters = method.GetParameters();
        if (arguments.Length > parameters.Length) throw new ArgumentException($"Too many arguments for {action}.");
        var values = new object?[parameters.Length];
        for (int index = 0; index < parameters.Length; index++)
        {
            var parameter = parameters[index];
            values[index] = index < arguments.Length ? CliValues.Convert(arguments[index], parameter.ParameterType, ResolveReference)
                : parameter.ParameterType == typeof(CancellationToken) ? _cancellation.Token
                : parameter.ParameterType == typeof(BuildMode) ? BuildMode.FullProcess
                : typeof(IList<GameFile>).IsAssignableFrom(parameter.ParameterType) ? _inputs
                : parameter.HasDefaultValue ? parameter.DefaultValue
                : throw new ArgumentException($"Action '{action}' needs argument '{parameter.Name}' ({CliValues.Describe(parameter.ParameterType)}).");
        }
        object? result = method.Invoke(target, values);
        if (result is Task task)
        {
            await task;
            result = task.GetType().GetProperty("Result")?.GetValue(task);
        }
        if (result is not null && result.GetType().Name != "VoidTaskResult")
            Emit(new { type = "action-result", action, value = CliValues.Snapshot(result) }, $"{action}: {result}");
    }

    private object? ResolveReference(string path) => path.Equals("inputs", StringComparison.OrdinalIgnoreCase) ? _inputs
        : path.StartsWith("inputs[", StringComparison.OrdinalIgnoreCase) ? CliValues.Get(new { inputs = _inputs }, path)
        : path.StartsWith("config.", StringComparison.OrdinalIgnoreCase) ? CliValues.Get(AppConfig.Instance, path[7..])
        : CliValues.Get(_model!, path);

    private void Subscribe(object model)
    {
        if (!_subscribed.Add(model)) return;
        if (model.GetType().GetProperty("LogEntries")?.GetValue(model) is ObservableCollection<LogEntry> logs)
            logs.CollectionChanged += (_, change) =>
            {
                if (change.NewItems is null) return;
                foreach (LogEntry entry in change.NewItems)
                {
                    if (!_seenLogs.Add(entry)) continue;
                    if (entry.Level == LogLevel.Error) _failed = true;
                    Emit(new { type = "log", level = entry.Level.ToString(), message = entry.Message }, entry.Message);
                }
            };
        foreach (var property in CliCatalog.Properties(model.GetType()).Where(property => typeof(ToolTabViewModel).IsAssignableFrom(property.PropertyType)))
            if (property.GetValue(model) is object child) Subscribe(child);
    }

    private void OnCancel(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        _cancelled = true;
        _cancellation.Cancel();
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (_model is ToolTabViewModel model && model.CancelCommand?.CanExecute(null) == true)
                model.CancelCommand.Execute(null);
            else _model?.GetType().GetMethod("Cancel", Type.EmptyTypes)?.Invoke(_model, null);
        });
    }

    private void OnDispatcherException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        _failed = true;
        Emit(new { type = "error", message = e.Exception.Message, exitCode = 1 }, e.Exception.Message);
        Application.Current.Shutdown(1);
    }

    private int Configure(string[] args)
    {
        if (args.Length < 2 || args[1] == "get")
        {
            WriteJson(CliValues.Snapshot(AppConfig.Instance));
            return 0;
        }
        if (args[1] != "set") throw new ArgumentException("Usage: config get | config set Section.Property=value");
        foreach (string assignment in args.Skip(2).Where(arg => arg != "--json"))
        {
            var values = new Dictionary<string, JsonElement>();
            AddAssignment(values, assignment);
            foreach (var (path, value) in values) CliValues.Set(AppConfig.Instance, path, value, ResolveReference);
        }
        AppConfig.PersistChanges = true;
        AppConfig.Instance.Save();
        WriteJson(CliValues.Snapshot(AppConfig.Instance));
        return 0;
    }

    private void Help(string? name)
    {
        if (name is not null)
        {
            if (name == "config")
            {
                Console.WriteLine("config get\nconfig set Section.Property=value\nSettings are saved beside RomForge.exe. Run-time --config overrides are not saved.");
                return;
            }
            var tool = CliCatalog.Find(name);
            if (_json) { WriteJson(CliCatalog.Schema(tool)); return; }
            Console.WriteLine($"{tool.Name}: {tool.Description}\nDefault action: {tool.DefaultAction}\n");
            Console.WriteLine(JsonSerializer.Serialize(CliCatalog.Schema(tool), new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("Set nested properties using --set FileItems[0].Property=value. Pass method parameters as a JSON array with --args. Use {\"$ref\":\"FileItems[0]\"} to reference registered objects.");
            return;
        }
        Console.WriteLine("""
            RomForge command-line interface (no arguments opens the GUI)

            Usage:
              RomForge.exe tools [--json]
              RomForge.exe help <tool>
              RomForge.exe <tool> [options]
              RomForge.exe run --job <job.json> [--json] [--yes]
              RomForge.exe config get
              RomForge.exe config set Section.Property=value

            Options:
              --input PATH           Register an input; repeat for multiple files/folders.
              --source PATH          Set the patch source file/folder.
              --patch PATH           Set a patch file/folder/archive.
              --source-entry PATH    Select an entry when a source archive is ambiguous.
              --format FORMAT        Select the output format for all registered file items.
              --set PATH=VALUE       Set a tool property; nested/indexed paths are supported.
              --config PATH=VALUE    Override a configuration value for this process only.
              --action NAME          Execute a method/command; repeat for sequential actions.
              --args JSON_ARRAY      Arguments for the preceding action.
              --job FILE             Read a JSON job (tool, inputs, set, config, format, steps).
              --json                 Emit newline-delimited JSON logs and final state.
              --yes                  Approve confirmation prompts, including destructive writes.
              --help                 Show English help and the tool's property/action schema.
              --version              Print the application version.

            Values may be plain strings or JSON. {"$ref":"FileItems[0]"} references state;
            {"$file":"path"} loads bytes for artwork. Action "inspect" returns state without running.
            Action "export" with [propertyPath, outputFile] exports bytes/artwork or JSON state.
            Ctrl+C cancels the active operation. Exit codes: 0 success, 1 operation failure,
            2 invalid arguments/job, 130 cancelled. CLI does not open folders or show dialogs.

            Examples:
              RomForge.exe patch --source "D:\games\game" --patch "D:\patch.zip" --json
              RomForge.exe arcade --source "D:\rom.zip" --patch "D:\patch.zip" --json
              RomForge.exe hash --input "D:\disc.iso" --set SelectedAlgorithm=SHA256 --json
              RomForge.exe compress --input "D:\game.rvz" --format iso --json
              RomForge.exe m3u --input "D:\games\disc1.chd" --input "D:\games\disc2.chd"
              RomForge.exe 3ds.repack --input "D:\game.cia" --set OutputPath="D:\work" --action StartAsync --args '["UnpackOnly"]'
            """);
    }

    private void Emit(object value, string message)
    {
        if (_json) WriteJson(value);
        else Console.WriteLine(message);
    }

    private static void WriteJson(object? value) => Console.WriteLine(JsonSerializer.Serialize(value));
}
