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
