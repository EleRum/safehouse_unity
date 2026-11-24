using System;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Safehouse.UI;

namespace Safehouse.Tests
{
    /// <summary>
    /// The audio that needs no audio device: the built-in sounds are plain float arrays, and a dropped-in
    /// .wav is read by <see cref="WaveFile"/> before it ever becomes an AudioClip. Both are testable here;
    /// whether the speakers are plugged in is not.
    /// </summary>
    public sealed class AudioTests
    {
        [Test]
        public void EveryBuiltInSoundIsAudibleAndInRange()
        {
            foreach (var id in ToneBank.Ids)
            {
                var samples = ToneBank.Build(id);

                Assert.IsNotNull(samples, id);
                Assert.Greater(samples.Length, 0, id);
                Assert.LessOrEqual(samples.Max(sample => Math.Abs(sample)), 1f, id + " must not clip");
                Assert.Greater(samples.Max(sample => Math.Abs(sample)), 0.02f, id + " must not be silence");
            }
        }

        [Test]
        public void TheTwoBedsAreOneWholeSeamlessLoop()
        {
            var length = (int)Math.Round(ToneBank.LoopSeconds * ToneBank.SampleRate);

            foreach (var id in new[] { "menu", "raid" })
            {
                var samples = ToneBank.Build(id);

                Assert.AreEqual(length, samples.Length, id);
                // One sample before the loop point must be almost the value at its start, or the loop clicks.
                Assert.Less(Math.Abs(samples[samples.Length - 1] - samples[0]), 0.08f, id + " clicks at the seam");
            }
        }

        [Test]
        public void AnUnknownIdHasNoSoundRatherThanAnException()
        {
            Assert.IsNull(ToneBank.Build("no-such-sound"));
            Assert.IsNull(ToneBank.Build(null));
        }

        [Test]
        public void A16BitWavIsReadBackAsTheSamplesItHolds()
        {
            var expected = Enumerable.Range(0, 200)
                .Select(index => (float)Math.Sin(index / 200.0 * Math.PI * 2) * 0.5f)
                .ToArray();

            var (samples, rate) = WaveFile.Parse(Wav(expected, rate: 22050));

            Assert.AreEqual(22050, rate);
            Assert.AreEqual(expected.Length, samples.Length);
            for (var index = 0; index < expected.Length; index += 25)
            {
                Assert.AreEqual(expected[index], samples[index], 0.001f, "sample " + index);
            }
        }

        [Test]
        public void StereoIsAveragedDownInsteadOfBeingPlayedTwiceAsFast()
        {
            // Left is +0.5, right is -0.5 everywhere: the mono mix is silence, and the frame count is half
            // the sample count.
            var interleaved = new float[200];
            for (var frame = 0; frame < 100; frame++)
            {
                interleaved[frame * 2] = 0.5f;
                interleaved[frame * 2 + 1] = -0.5f;
            }

            var (samples, _) = WaveFile.Parse(Wav(interleaved, channels: 2));

            Assert.AreEqual(100, samples.Length);
            Assert.Less(samples.Max(sample => Math.Abs(sample)), 0.01f);
        }

        [Test]
        public void SomethingThatIsNotAWavSaysSo()
        {
            Assert.Throws<InvalidDataException>(() => WaveFile.Parse(Encoding.ASCII.GetBytes("not a wav file at all, really")));

            var truncated = Wav(new[] { 0.1f, 0.2f });
            truncated[0] = (byte)'X';
            Assert.Throws<InvalidDataException>(() => WaveFile.Parse(truncated));
        }

        /// <summary>A minimal PCM .wav in memory: the smallest thing WaveFile is expected to accept.</summary>
        private static byte[] Wav(float[] interleaved, int rate = 44100, int channels = 1)
        {
            var data = interleaved.Length * 2;
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);

            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + data);
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1);                                // PCM
            writer.Write((short)channels);
            writer.Write(rate);
            writer.Write(rate * channels * 2);                     // byte rate
            writer.Write((short)(channels * 2));                   // block align
            writer.Write((short)16);                               // bits
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(data);
            foreach (var sample in interleaved)
            {
                writer.Write((short)Math.Round(Math.Clamp(sample, -1f, 1f) * 32767));
            }

            writer.Flush();
            return stream.ToArray();
        }
    }
}
