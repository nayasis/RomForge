using Patch.Core;
using Patch.Core.Enums;
using System.IO;

namespace RomForge.Core.Services.Patch;

public static class PatchSourceValidation
{
    public static async Task<bool> SupportsDirectCompressedSourceAsync(string patchPath, CancellationToken ct)
    {
        PatchFormat format = await UniversalPatcher.DetectFormatAsync(patchPath, ct);

        return format is PatchFormat.Bps or PatchFormat.Ups or PatchFormat.Xdelta
            || HasPpfBlockCheck(patchPath, format);
    }

    private static bool HasPpfBlockCheck(string patchPath, PatchFormat format)
    {
        if (format != PatchFormat.Ppf)
            return false;

        using var stream = new FileStream(patchPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var header = new byte[60];

        if (stream.Read(header, 0, header.Length) != header.Length)
            return false;

        int version = header[3] - '0';

        return version == 2 || version == 3 && header[57] != 0;
    }
}
