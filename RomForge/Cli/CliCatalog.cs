using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Common.WPF.ViewModels;
using RomForge.ViewModels;
using RomForge.ViewModels.Patch;
using RomForge.ViewModels.Util;

namespace RomForge.Cli;

internal sealed record CliTool(string Name, Type Type, string Description, string DefaultAction = "RunCommand");

internal static class CliCatalog
{
    public static readonly CliTool[] Tools =
    [
        new("patch", typeof(NormalPatchMainViewModel), "Apply a patch or archived patch set; supports source folders, multiple discs, compression and EL packages.", "RunAsync"),
        new("arcade", typeof(ArcadePatchMainViewModel), "Match and patch ROM entries in ZIP/7Z archives; supports IPS, BPS and Xdelta.", "RunAsync"),
        new("compress", typeof(CompressMainViewModel), "Compress/decompress disc and ROM images including CHD, RVZ, GCZ, WBFS, ZIP, NSZ and ZCCI."),
        new("convert", typeof(ConvertMainViewModel), "Convert supported console/container formats, including CCD/MDS disc descriptors."),
        new("switch.repack", typeof(ViewModels.Switch.RepackMainViewModel), "Unpack, patch, edit metadata and rebuild Switch titles.", "StartAsync"),
        new("switch.merge", typeof(ViewModels.Switch.MergeMainViewModel), "Merge or split Switch base games, updates and DLC.", "MergeAsync"),
        new("switch.convert", typeof(ViewModels.Switch.ConverterMainViewModel), "Convert/compress/decompress Switch containers."),
        new("switch.saturn", typeof(ViewModels.Switch.ConvertSaturnMainViewModel), "Build a Saturn game NSP using a base NSP and CUE image.", "ConvertAsync"),
        new("switch.keygen", typeof(ViewModels.Switch.KeygenMainViewModel), "Copy/re-encrypt Switch titles using key generation zero.", "ExecuteStartWorkAsync"),
        new("wiiu.repack", typeof(ViewModels.WiiU.RepackMainViewModel), "Unpack, patch and rebuild Wii U WUA/WUP titles.", "StartAsync"),
        new("wiiu.convert", typeof(ViewModels.WiiU.ConverterMainViewModel), "Convert Wii U WUD, WUX, WUA and game folders."),
        new("3ds.repack", typeof(ViewModels._3DS.RepackMainViewModel), "Unpack, patch and rebuild 3DS CCI/ZCCI/CIA titles.", "StartAsync"),
        new("3ds.convert", typeof(ViewModels._3DS.ConverterMainViewModel), "Convert 3DS title containers."),
        new("3ds.decrypt", typeof(ViewModels._3DS.DecryptorMainViewModel), "Decrypt 3DS titles."),
        new("3ds.sd", typeof(ViewModels._3DS.InstallerMainViewModel), "List, filter, extract and install titles on a 3DS SD card using movable.sed.", "LoadTitlesAsync"),
        new("ps.pack", typeof(ViewModels.PS.PackingMainViewModel), "Build a PS1 PBP with disc order, artwork and compatibility options."),
        new("ps.unpack", typeof(ViewModels.PS.UnpackingMainViewModel), "Extract PS1 discs from PBP containers."),
        new("psp.convert", typeof(ViewModels.PS.PSPConverterViewModel), "Convert PSP ISO/CSO/PBP containers."),
        new("hash", typeof(HashMainViewModel), "Calculate MD5, SHA1, SHA256, CRC32 or BLAKE3 hashes."),
        new("cue", typeof(CueMainViewModel), "Generate CUE descriptors for BIN images."),
        new("m3u", typeof(M3uMainViewModel), "Generate/overwrite M3U playlists grouped by directory; supports reordering."),
        new("certs", typeof(CertsMainViewModel), "Extract certs.bin from a CIA title."),
        new("tistory", typeof(TistoryMainViewModel), "Analyze attachment links and download with queued retries, resume and extraction."),
        new("disk", typeof(ZipImageToolMainViewModel), "List removable drives, save images and write ZIP/GZ/IMG images. Writing requires --yes.", "ReadAsync")
    ];

    public static CliTool Find(string name) => Tools.FirstOrDefault(tool => tool.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"Unknown tool '{name}'. Run 'RomForge.exe tools'.");

    public static IEnumerable<MethodInfo> Methods(Type type) => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        .Where(method => !method.IsSpecialName && !method.Name.Contains("Log") && !method.Name.StartsWith("Navigate")
            && !method.Name.StartsWith("Browse") && !method.Name.StartsWith("GetFileDialog")
            && !method.GetParameters().Any(parameter => parameter.ParameterType.IsByRef)
            && !(method.ReturnType == typeof(void) && method.GetCustomAttribute<AsyncStateMachineAttribute>() is not null));

    public static IEnumerable<PropertyInfo> Properties(Type type) => type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
        .Where(property => property.GetIndexParameters().Length == 0 && property.GetMethod is not null
            && !typeof(Delegate).IsAssignableFrom(property.PropertyType) && property.Name != "Tools"
            && !property.Name.EndsWith("Visibility"));

    public static object Schema(CliTool tool) => new
    {
        tool.Name,
        tool.Description,
        tool.DefaultAction,
        properties = PropertySchema(tool.Type, 0),
        actions = ActionSchema(tool.Type, "", 0).Concat(new object[]
        {
            new { name = "inspect", parameters = Array.Empty<object>() },
            new { name = "export", parameters = new[] { new { name = "propertyPath", type = "String" }, new { name = "outputFile", type = "String" } } }
        }).ToArray()
    };

    private static object[] PropertySchema(Type type, int depth) => Properties(type)
        .Where(property => !typeof(ICommand).IsAssignableFrom(property.PropertyType))
        .Select(property => (object)new
        {
            name = property.Name,
            type = CliValues.Describe(property.PropertyType),
            writable = property.SetMethod?.IsPublic == true,
            children = depth < 2 && IsModel(property.PropertyType) ? PropertySchema(ElementType(property.PropertyType), depth + 1) : []
        }).ToArray();

    private static object[] ActionSchema(Type type, string prefix, int depth)
    {
        var actions = Properties(type).Where(property => typeof(ICommand).IsAssignableFrom(property.PropertyType)
                && !property.Name.StartsWith("Browse") && !property.Name.StartsWith("Open") && !property.Name.Contains("Settings"))
            .Select(property => (object)new { name = prefix + property.Name, parameters = Array.Empty<object>() }).ToList();
        actions.AddRange(Methods(type).Select(method => (object)new
        {
            name = prefix + method.Name,
            parameters = method.GetParameters().Select(parameter => new
            {
                parameter.Name,
                type = CliValues.Describe(parameter.ParameterType),
                optional = parameter.IsOptional || parameter.ParameterType == typeof(CancellationToken)
            }).ToArray()
        }));
        if (depth < 1)
            foreach (var property in Properties(type).Where(property => typeof(ToolTabViewModel).IsAssignableFrom(property.PropertyType)))
                actions.AddRange(ActionSchema(property.PropertyType, prefix + property.Name + ".", depth + 1));
        return actions.ToArray();
    }

    private static Type ElementType(Type type) => type.IsGenericType ? type.GetGenericArguments()[0] : type;
    private static bool IsModel(Type type) => ElementType(type).Namespace is string ns
        && (ns.StartsWith("RomForge") || ns.StartsWith("NSW") || ns.StartsWith("Common.WPF") || ns.StartsWith("LibHac"))
        && !ElementType(type).IsEnum;
}
