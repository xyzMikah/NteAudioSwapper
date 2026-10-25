using System;
using System.IO;
using NAudio.Wave;

namespace NtePakTool
{
    /// <summary>
    /// Turns any audio file Windows can decode (wav/mp3/flac/m4a/aac/wma)
    /// into a clean 48 kHz 16-bit stereo PCM WAV, ready for wav2wem.exe.
    /// No Wwise, no FFmpeg.
    /// </summary>
    public static class WemEncoder
    {
        public const int SampleRate = 48000;
        public const int Channels   = 2;

        public static readonly string[] SupportedExtensions =
            { ".wav", ".mp3", ".flac", ".m4a", ".aac", ".wma" };

        public const string FileFilter =
            "Audio Files|*.wav;*.mp3;*.flac;*.m4a;*.aac;*.wma|All Files|*.*";

        public static bool IsSupported(string path) =>
            Array.Exists(SupportedExtensions,
                e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// wav2wem only matches real Wwise for MONO files. For stereo it writes
        /// channel config 0 and a different codebook id / alloc sizes, and NTE's
        /// Wwise refuses to play it. This rewrites those header fields to the
        /// exact values found in .wem files made by real Wwise for NTE.
        /// </summary>
        public static void PatchWwiseHeader(string wemPath)
        {
            byte[] b = File.ReadAllBytes(wemPath);

            // RIFF....WAVE + "fmt " chunk of 0x42 bytes, Wwise Vorbis (0xFFFF)
            if (b.Length < 0x56 ||
                b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F' ||
                b[12] != 'f' || b[13] != 'm' || b[14] != 't' ||
                BitConverter.ToUInt32(b, 16) != 0x42 ||
                BitConverter.ToUInt16(b, 20) != 0xFFFF)
                throw new Exception($"{Path.GetFileName(wemPath)} is not a Wwise Vorbis .wem");

            const int fmt = 20;                       // start of fmt data
            int channels  = BitConverter.ToUInt16(b, fmt + 0x02);
            int blockExp1 = b[fmt + 0x41];            // big block size (2^n)

            // 0x14 channel config: [numCh:8][type:4][mask:20] like Wwise does
            uint cfg = channels switch
            {
                1 => 0x4101u,   // mono   (center)
                2 => 0x3102u,   // stereo (front left + right)
                _ => BitConverter.ToUInt32(b, fmt + 0x14),
            };
            Write32(b, fmt + 0x14, cfg);

            // 0x34 / 0x38 decoder memory sizes (real Wwise: 16080 / 16560 for stereo, 2048 blocks)
            double scale = channels / 2.0 * Math.Pow(2, blockExp1 - 11);
            Write32(b, fmt + 0x34, (uint)Math.Round(16080 * scale));
            Write32(b, fmt + 0x38, (uint)Math.Round(16560 * scale));

            // 0x08 avg bytes/sec: Wwise uses audio bytes (0x20) * rate / samples
            uint rate     = BitConverter.ToUInt32(b, fmt + 0x04);
            uint samples  = BitConverter.ToUInt32(b, fmt + 0x18);
            uint setupLen = BitConverter.ToUInt32(b, fmt + 0x1C);
            uint audioEnd = BitConverter.ToUInt32(b, fmt + 0x20);
            uint seekSize = BitConverter.ToUInt32(b, fmt + 0x28);
            if (samples > 0)
                Write32(b, fmt + 0x08, (uint)Math.Round((double)audioEnd * rate / samples));

            // 0x3C setup hash: real Wwise gives every file its own value, so
            // derive a unique one from this file's Vorbis setup bytes (FNV-1a)
            int data = FindChunk(b, "data");
            uint hash = 2166136261;
            if (data > 0)
            {
                int start = data + 8 + (int)seekSize;
                int end   = Math.Min(b.Length, start + (int)setupLen);
                for (int i = start; i < end; i++) { hash ^= b[i]; hash *= 16777619; }
                hash ^= samples; hash *= 16777619;
            }
            Write32(b, fmt + 0x3C, hash);

            File.WriteAllBytes(wemPath, b);
        }

        private static int FindChunk(byte[] b, string id)
        {
            int p = 12;
            while (p + 8 <= b.Length)
            {
                if (b[p] == id[0] && b[p + 1] == id[1] && b[p + 2] == id[2] && b[p + 3] == id[3]) return p;
                uint size = BitConverter.ToUInt32(b, p + 4);
                p += 8 + (int)size + (int)(size & 1);
            }
            return -1;
        }

        private static void Write32(byte[] b, int at, uint v) =>
            BitConverter.GetBytes(v).CopyTo(b, at);

        /// <summary>Decode + resample to 48k/16-bit/stereo WAV.</summary>
        public static void NormalizeToWav(string input, string outputWav)
        {
            var target = new WaveFormat(SampleRate, 16, Channels);

            // MediaFoundation handles mp3/m4a/aac/wma/flac (Win10+) and odd WAVs (float, 24-bit, extensible)
            try
            {
                using var reader    = new MediaFoundationReader(input);
                using var resampler = new MediaFoundationResampler(reader, target) { ResamplerQuality = 60 };
                WaveFileWriter.CreateWaveFile(outputWav, resampler);
                return;
            }
            catch (Exception mfEx)
            {
                // Fallback for plain WAVs if MediaFoundation is missing (e.g. Windows N editions)
                if (!input.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                    throw new Exception($"Can't decode {Path.GetFileName(input)}: {mfEx.Message}");

                using var reader = new AudioFileReader(input);
                var resampled = new NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(
                    reader.WaveFormat.Channels == 1
                        ? (ISampleProvider)new NAudio.Wave.SampleProviders.MonoToStereoSampleProvider(reader)
                        : reader,
                    SampleRate);
                WaveFileWriter.CreateWaveFile16(outputWav, resampled);
            }
        }
    }
}
