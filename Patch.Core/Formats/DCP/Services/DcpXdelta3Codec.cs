using System.Runtime.InteropServices;

namespace Patch.Core.Formats.DCP.Services;

// Uses Universal Dreamcast Patcher's xdelta3 native interface for DCP payloads.
internal static class DcpXdelta3Codec
{
    private const int NoSpaceError = 28;
    private const long MaxOutputSize = 2L * 1024 * 1024 * 1024 - 1024;

    [DllImport("udp_xdelta3", CallingConvention = CallingConvention.Cdecl, EntryPoint = "xd3_decode_memory")]
    private static extern unsafe int DecodeMemory(
        byte* input,
        nuint inputSize,
        byte* source,
        nuint sourceSize,
        byte* output,
        out nuint outputSize,
        nuint outputCapacity,
        int flags);

    public static unsafe byte[] Decode(byte[] source, byte[] patch)
    {
        long bufferSize = Math.Min(Math.Max(source.LongLength * 2 + 65536, 65536), MaxOutputSize);

        while (true)
        {
            var output = new byte[checked((int)bufferSize)];
            int result;
            nuint written;

            fixed (byte* inputPointer = patch)
            fixed (byte* sourcePointer = source.Length > 0 ? source : new byte[1])
            fixed (byte* outputPointer = output)
            {
                result = DecodeMemory(
                    inputPointer,
                    (nuint)patch.LongLength,
                    sourcePointer,
                    (nuint)source.LongLength,
                    outputPointer,
                    out written,
                    (nuint)output.LongLength,
                    0);
            }

            if (result == 0)
            {
                if (written > (nuint)output.LongLength)
                    throw new InvalidOperationException("xdelta3가 출력 버퍼 범위를 초과했습니다.");

                if ((long)written == output.LongLength)
                    return output;

                return output[..checked((int)written)];
            }

            if (result != NoSpaceError)
                throw new InvalidOperationException($"Universal Dreamcast Patcher xdelta 디코드 실패: {result}");

            if (bufferSize >= MaxOutputSize)
                throw new InvalidOperationException($"xdelta 출력이 지원 가능한 최대 크기({MaxOutputSize:N0} bytes)를 초과했습니다.");

            bufferSize = Math.Min(bufferSize * 2, MaxOutputSize);
        }
    }
}
