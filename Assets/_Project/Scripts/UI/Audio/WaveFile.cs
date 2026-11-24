using System;
using System.IO;

namespace Safehouse.UI
{
    /// <summary>
    /// A plain PCM .wav reader, because sounds are dropped next to the data files as ordinary files
    /// (<c>StreamingAssets/audio</c>) rather than imported as Unity assets: the same reason items.json and
    /// the saves are hand-editable JSON. Only what the game needs - 8 or 16 bit PCM, any channel count -
    /// and a clear exception for anything else, so a wrong file does not fail silently.
    /// </summary>
    public static class WaveFile
    {
        public const int MinSampleRate = 8000;
        public const int MaxSampleRate = 192000;

        /// <summary>
        /// The samples as one mono channel, scaled to -1..1, and the rate they were recorded at. Stereo and
        /// more are averaged down: everything in this game is a one-shot or a bed, and the AudioSource pans
        /// nothing, so the extra channels would only cost memory.
        /// </summary>
        public static (float[] Samples, int SampleRate) Parse(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 44)
            {
                throw new InvalidDataException("A .wav file needs at least a 44-byte header.");
            }

            if (ReadTag(bytes, 0) != "RIFF" || ReadTag(bytes, 8) != "WAVE")
            {
                throw new InvalidDataException("That is not a RIFF/WAVE file.");
            }

            var channels = 0;
            var bits = 0;
            var format = 0;
            var rate = 0;
            var dataOffset = -1;
            var dataLength = 0;

            var cursor = 12;
            while (cursor + 8 <= bytes.Length)
            {
                var id = ReadTag(bytes, cursor);
                var size = (int)ReadUInt32(bytes, cursor + 4);
                var body = cursor + 8;
                if (size < 0 || body + size > bytes.Length)
                {
                    size = bytes.Length - body; // a truncated last chunk: read what is actually there
                }

                if (id == "fmt " && size >= 16)
                {
                    format = ReadUInt16(bytes, body);
                    channels = ReadUInt16(bytes, body + 2);
                    rate = (int)ReadUInt32(bytes, body + 4);
                    bits = ReadUInt16(bytes, body + 14);
                    if (format == 0xFFFE && size >= 26)
                    {
                        format = ReadUInt16(bytes, body + 24); // WAVE_FORMAT_EXTENSIBLE says what it really is
                    }
                }
                else if (id == "data")
                {
                    dataOffset = body;
                    dataLength = size;
                }

                cursor = body + size + (size % 2); // chunks are word-aligned
            }

            if (format != 1)
            {
                throw new InvalidDataException($"Only uncompressed PCM .wav files are read (this one is format {format}).");
            }

            if (channels < 1 || rate < MinSampleRate || rate > MaxSampleRate)
            {
                throw new InvalidDataException($"That .wav's format looks wrong ({channels} channels at {rate} Hz).");
            }

            if (bits != 8 && bits != 16)
            {
                throw new InvalidDataException($"Only 8 and 16 bit .wav files are read (this one is {bits} bit).");
            }

            if (dataOffset < 0 || dataLength <= 0)
            {
                throw new InvalidDataException("That .wav has no audio in it.");
            }

            var bytesPerSample = bits / 8;
            var frames = dataLength / (bytesPerSample * channels);
            var samples = new float[frames];
            for (var frame = 0; frame < frames; frame++)
            {
                var sum = 0f;
                for (var channel = 0; channel < channels; channel++)
                {
                    var at = dataOffset + (frame * channels + channel) * bytesPerSample;
                    sum += bits == 16
                        ? BitConverter.ToInt16(bytes, at) / 32768f
                        : (bytes[at] - 128) / 128f;
                }

                samples[frame] = sum / channels;
            }

            return (samples, rate);
        }

        private static string ReadTag(byte[] bytes, int at) =>
            new string(new[] { (char)bytes[at], (char)bytes[at + 1], (char)bytes[at + 2], (char)bytes[at + 3] });

        private static int ReadUInt16(byte[] bytes, int at) => bytes[at] | (bytes[at + 1] << 8);

        private static uint ReadUInt32(byte[] bytes, int at) =>
            (uint)(bytes[at] | (bytes[at + 1] << 8) | (bytes[at + 2] << 16) | (bytes[at + 3] << 24));
    }
}
