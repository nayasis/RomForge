# Command-line interface

RomForge supports unattended commands in the same executable as the desktop app.
Run it without arguments to open the GUI. Commands run without a window or file
dialogs and finish with an exit code. Keys and configuration are resolved beside
the executable, just as in the desktop app.

## Discover capabilities

```powershell
$rf = 'D:\app\SimpleLauncherApp\RomForge\RomForge.exe'
& $rf --help
& $rf tools --json
& $rf help arcade
& $rf help switch.repack --json
```

`tools --json` lists the current tool catalog with property types, enum values,
collection item properties, commands and method parameters. Use `help <tool>` for
one tool. English help is generated from the same catalog used for execution.

PowerShell may return immediately for a GUI executable when invoked interactively.
Use a pipeline (`& $rf --help | Out-Host`) or `Start-Process -Wait` when you need to
wait. Automation should start the process with redirected stdout/stderr and wait
for its exit. Output is UTF-8.

## UI capabilities and CLI tools

| UI feature | CLI tool | Default action / other actions |
| --- | --- | --- |
| General patch, archives, multiple discs, EL package | `patch` | `RunAsync` |
| Arcade entry matching and patching | `arcade` | `RunAsync`, `ManualMatch` |
| Compression/decompression | `compress` | `RunCommand`, `ToggleOutputExtension` |
| Unified format conversion | `convert` | `RunCommand` |
| Switch unpack/patch/rebuild, language/title/artwork edits | `switch.repack` | `StartAsync` |
| Switch merge/split | `switch.merge` | `MergeAsync`, `SplitAsync` |
| Switch format conversion/compression | `switch.convert` | `RunCommand` |
| Saturn-to-Switch NSP | `switch.saturn` | `ConvertAsync` |
| Switch key generation zero copy | `switch.keygen` | `ExecuteStartWorkAsync` |
| Wii U unpack/patch/rebuild | `wiiu.repack` | `StartAsync` |
| Wii U conversion | `wiiu.convert` | `RunCommand` |
| 3DS unpack/patch/rebuild and metadata edits | `3ds.repack` | `StartAsync` |
| 3DS conversion | `3ds.convert` | `RunCommand` |
| 3DS decryption | `3ds.decrypt` | `RunCommand` |
| 3DS SD scan/filter/extract/install | `3ds.sd` | `LoadTitlesAsync`, `ExtractTitleAsync`, `InstallAsync`, nested `Install.*` and `InstalledTitles.*` actions |
| PS1 PBP creation, artwork, disc order, fixes | `ps.pack` | `RunCommand`, `SetIcon0FromBytes`, `SetPic0FromBytes`, `SetPic1FromBytes`, `SetBootLogoFromBytes`, resets |
| PS1 PBP extraction | `ps.unpack` | `RunCommand` |
| PSP conversion | `psp.convert` | `RunCommand` |
| Hash calculation | `hash` | `RunCommand` |
| CUE generation | `cue` | `RunCommand` |
| M3U generation/overwrite and within-group ordering | `m3u` | `RunCommand`, `MoveItem` |
| CIA certificate extraction | `certs` | `RunCommand` |
| Tistory analyze/select/download/retry/resume/extract | `tistory` | `AnalyzeCommand`, `RunCommand` |
| USB image read/write and drive selection | `disk` | `ReadAsync`, `WriteAsync` |
| All application settings | `config` | `get`, `set` |

UI-only actions such as choosing files, editing a selection or dragging list items
are represented by registering paths, setting properties, referencing items and
calling list methods. Every tool also supports `inspect` and `export`.

## Common options

| Option | Purpose |
| --- | --- |
| `--input PATH` | Register a file/folder; repeat for multiple inputs. For `tistory`, input is a page URL. |
| `--source PATH`, `--patch PATH` | General/arcade patch source and patch path. |
| `--source-entry PATH` | Select an entry when a source archive has multiple candidates. |
| `--format FORMAT` | Select a supported target format for all registered file items. |
| `--set PATH=VALUE` | Set tool, item or metadata properties. Values may be strings or JSON. |
| `--config PATH=VALUE` | Override an application setting for this process only. |
| `--action NAME` | Execute a command or method; repeat for sequential actions. |
| `--args JSON_ARRAY` | Arguments to the preceding action. |
| `--job FILE` | Load a JSON job with tool, inputs, options and ordered steps. |
| `--json` | Emit newline-delimited JSON events and final state. |
| `--yes` | Approve confirmation prompts, including overwrites or disk writes. |

Run-time options do not change the saved GUI settings. Use `config set` to persist
settings. Folder opening is suppressed for CLI operations.

```powershell
& $rf config get
& $rf config set 'Patch.AutoCompress=true'
& $rf hash --input 'D:\games\disc.iso' --set SelectedAlgorithm=SHA256 --json
& $rf compress --input 'D:\games\game.rvz' --format iso --json
& $rf patch --source 'D:\games\game' --patch 'D:\patch.zip' `
  --config Patch.AutoCompress=true --config Patch.CreateElPackageWhenCompleted=true --json
& $rf arcade --source 'D:\roms\game.zip' --patch 'D:\patches\game.zip' --json
& $rf tistory --input 'https://example.tistory.com/123' `
  --set SaveDirectory='D:\downloads' --set AutoExtractAfterDownload=true --json
```

General, arcade and multi-disc patch operations use `output_HHmmss`, where the
six digits are the local time when the patch operation started. All tracks and
temporary source files stay in that operation's folder; EL package cleanup removes
that folder. Final game/disc filenames retain their normal naming rules.

## Inspect, select and customize items

```powershell
& $rf convert --input 'D:\game.cia' --action inspect --json
& $rf convert --input 'D:\game.cia' --set 'FileItems[0].SelectedTargetFormat=CCI'
& $rf ps.pack --input 'D:\disc1.cue' --set GameTitle='Translated Game' `
  --action SetIcon0FromBytes --args '[{"$file":"D:\cover.png"}]' --action RunCommand
```

`{"$ref":"FileItems[0]"}` references the live registered item. References can be
used in method arguments, arrays or property assignments. `{"$file":"D:\file"}`
loads bytes for artwork fields/methods. `inputs` is the imported Switch title list;
use `inputs[0].PatchPath` to assign patches for Switch repack/merge operations.
Set `Context.Metadata.Languages[0].TitleName`, `Publisher`, `Flag` and `LogoData`
to customize Switch language metadata. `Context.ForcedLanguage`, `TargetIdOffset`
and `OverrideTitleId` expose the corresponding UI settings. If rebuilding existing
unpacked Switch data, supply `Context` as JSON and `OutputPath` explicitly.

`export` takes `[propertyPath, outputFile]`: byte arrays are written as bytes;
other values are exported as JSON. For example:

```powershell
& $rf ps.pack --input 'D:\disc1.cue' --action export --args '["Icon0Bytes","D:\cover.png"]'
```

## JSON jobs and ordered steps

Save a UTF-8 JSON file and run `RomForge.exe run --job job.json --json`.
Inputs are registered first, top-level `set` properties are then applied, and
steps run in order. Each step can change properties before invoking its action.
If steps are omitted, the tool's default action runs.

An M3U job that places Disc 2 before Disc 1:

```json
{
  "tool": "m3u",
  "inputs": ["D:\\games\\Disc 1.chd", "D:\\games\\Disc 2.chd"],
  "steps": [
    {"action": "MoveItem", "args": [{"$ref":"FileItems[1]"}, {"$ref":"FileItems[0]"}, false]},
    {"action": "RunCommand"}
  ]
}
```

A 3DS SD scan followed by extracting the first filtered title:

```json
{
  "tool": "3ds.sd",
  "set": {"SdPath":"E:\\", "MovablePath":"D:\\movable.sed"},
  "steps": [
    {"action":"LoadTitlesAsync"},
    {"action":"ExtractTitleAsync", "args":[{"$ref":"InstalledTitles.FilteredTitles[0]"}, "D:\\game.cia", false]}
  ]
}
```

Use `Install.AddFiles` with an array of title paths before `InstallAsync`, referencing
an item from `Install.Items`. Set the same region, language and patch properties
shown by `help 3ds.sd` before installation. Reinstallation prompts require `--yes`.

Repack actions accept `"UnpackOnly"`, `"RebuildOnly"` or `"FullProcess"` as the
first argument. With no argument, `StartAsync` uses `FullProcess`.

For Tistory, `RunCommand` analyzes first if no attachments are registered. To select
individual attachments, use separate `AnalyzeCommand`, step-level property changes
such as `FileItems[0].IsSelected=false`, then `RunCommand`.

## Disk imaging

First run `disk --action inspect --json` to list `UsbDrives`. Explicitly assign a
drive using `--set 'SelectedDrive={"$ref":"UsbDrives[0]"}'`. Reading also requires
`--set ReadImagePath=...`; writing requires an input image and `--yes`. Disk actions
do not choose the default detected drive implicitly. Drive lists may change, so
read back the selected disk number and capacity before issuing a write job.

## Output and exit codes

JSON output contains `log`, `notification`, `action-result`, `export`, `error` and
`result` events. The final result includes `success`, tool state and imported
Switch inputs. State includes list status, progress, hashes, match information and
path properties. Logs identify generated output paths. Binary artwork and passwords
are omitted from JSON state; use `export` to retrieve artwork.

| Exit code | Meaning |
| --- | --- |
| 0 | Successful operation or help/inspection |
| 1 | Operation failure, error log or required confirmation |
| 2 | Invalid argument, property, job or input path |
| 130 | Cancellation |

Ctrl+C requests cancellation of the active operation. An agent should wait for the
process to exit and inspect both its exit code and final JSON result before
reporting success. Native libraries can also write diagnostics to stderr.
