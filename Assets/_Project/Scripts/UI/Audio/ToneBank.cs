using System;
using System.Collections.Generic;

namespace Safehouse.UI
{
    /// <summary>
    /// The sounds the game makes when nobody has supplied any: every effect and both music beds are
    /// synthesised here, so the game is audible with no audio assets at all. Drop a
    /// <c>StreamingAssets/audio/&lt;id&gt;.wav</c> with the same id and <see cref="AudioDirector"/> plays that
    /// instead - the synth is the floor, not the plan.
    ///
    /// The palette is a field radio and a steel bunker rather than a synthesiser: key clicks instead of
    /// beeps, squelch instead of buzzers, webbing and crate latches instead of chimes, and two low beds
    /// with static and distant artillery under them.
    ///
    /// Everything here is pure arithmetic over a float[]: no AudioClip, no engine objects, so the shapes
    /// can be tested and measured without an audio device (see <c>AudioTests</c>).
    /// </summary>
    public static class ToneBank
    {
        public const int SampleRate = 44100;

        /// <summary>Both beds are exactly this long, and every frequency they use is a whole multiple of
        /// 1/16 Hz, so the loop joins itself without a click at the seam.</summary>
        public const double LoopSeconds = 16.0;

        /// <summary>Every id <see cref="Build"/> knows, which is also the set of file names it looks for.</summary>
        public static readonly IReadOnlyList<string> Ids = new[]
        {
            "click", "error", "move", "equip", "trash", "case", "heal", "trade", "shot",
            "heartbeat", "death", "extract", "menu", "raid",
        };

        /// <summary>The samples for one id, or null when the id is not one of <see cref="Ids"/>.</summary>
        public static float[] Build(string id)
        {
            switch (id)
            {
                // A dry toggle: no pitch, just something being pressed and let go.
                case "click": return Render(0.09, (t, i) => Key(t, i, 1500) * 0.42);

                // A squelch bursting out of a handset: band-shaped noise over a low body, no "error" bleep.
                case "error": return Render(0.38, (t, i) => Noise(i, 23) * (0.45 + 0.55 * Math.Sin(TwoPi * 170 * t))
                                                          * Decay(t, 9) * 0.30
                                                          + Math.Sin(TwoPi * 132 * t) * Decay(t, 12) * 0.20);

                // Webbing and a pack shifting.
                case "move": return Render(0.17, (t, i) => Noise(i, 31) * (0.40 + 0.60 * Math.Sin(TwoPi * 62 * t))
                                                         * Decay(t, 14) * 0.26
                                                         + Math.Sin(TwoPi * 150 * t) * Decay(t, 30) * 0.10);

                // Steel on steel: the clack of the hit, then the ring of the metal.
                case "equip": return Render(0.40, (t, i) => Math.Sin(TwoPi * 1740 * t) * Decay(t, 14) * 0.16
                                                           + Math.Sin(TwoPi * 2660 * t) * Decay(t, 24) * 0.11
                                                           + Noise(i, 41) * Decay(t, 40) * 0.24
                                                           + Math.Sin(TwoPi * 176 * t) * Decay(t, 11) * 0.16);

                // Plastic and paper being crushed.
                case "trash": return Render(0.40, (t, i) => Noise(i, 53) * (0.50 + 0.50 * Math.Sin(TwoPi * 34 * t))
                                                           * Decay(t, 5) * 0.30);

                // A crate latch: the wooden knock first, the metal click after it.
                case "case": return Render(0.62, (t, i) => Math.Sin(TwoPi * 138 * t) * Decay(t, 17) * 0.30
                                                          + Noise(i, 67) * Decay(t, 26) * 0.16
                                                          + Key(t - 0.13, i, 2100) * 0.20);

                // Field dressing: tape and gauze tearing, rough rather than musical.
                case "heal": return Render(0.42, (t, i) => Noise(i, 89) * (0.30 + 0.70 * Math.Abs(Math.Sin(TwoPi * 26 * t)))
                                                          * Envelope(t, 0.012, 0.40) * 0.26);

                // A rifle: the crack, the body of it, and a short tail.
                case "shot": return Render(0.30, (t, i) => Noise(i, 79) * Decay(t, 42) * 0.46
                                                          + Math.Sin(TwoPi * 78 * t) * Decay(t, 16) * 0.34
                                                          + Noise(i, 83) * Decay(t, 6) * 0.10);

                // A till: the drawer thunk first, then the small bell of the bell-hammer on the way out.
                case "trade": return Render(0.90, (t, i) => Math.Sin(TwoPi * 118 * t) * Decay(t, 15) * 0.26
                                                          + Noise(i, 43) * Decay(t, 30) * 0.18
                                                          + (t > 0.20 ? Math.Sin(TwoPi * 2093 * (t - 0.20))
                                                              * Decay(t - 0.20, 7) * 0.14 : 0));

                // The heartbeat, for the synth fallback: lub, dub, then a beat of nothing, so it loops.
                case "heartbeat": return Render(2.0, (t, i) => Thump(t, 58, 9) * 0.55
                                                            + Thump(t - 0.30, 47, 11) * 0.40);

                // Going down: the low hit, and the room going quiet behind it.
                case "death": return Render(1.70, (t, i) => Tone(230, 48, t) * Decay(t, 1.4) * 0.32
                                                           + Math.Sin(TwoPi * 39 * t) * Decay(t, 1.0) * 0.20
                                                           + Noise(i, 97) * Decay(t, 1.6) * 0.10);

                // "Copy that": two short chirps over the radio, the second one higher.
                case "extract": return Render(0.52, (t, i) => t < 0.11
                    ? Math.Sin(TwoPi * 980 * t) * Envelope(t, 0.008, 0.10) * 0.26
                    : t > 0.17
                        ? Math.Sin(TwoPi * 1310 * (t - 0.17)) * Envelope(t - 0.17, 0.008, 0.22) * 0.26
                        : 0);

                // Both beds are post-rock: four slow swells of tremolo-picked chords, with the repeats fed
                // back inside the loop itself. The raid version sits lower, moves slower and keeps a pulse.
                case "menu": return PostRock(false);

                case "raid": return PostRock(true);
                default: return null;
            }
        }

        private const double TwoPi = Math.PI * 2;

        // ---- the beds: two post-rock swells ----

        /// <summary>One chord per bar, four bars a loop, so a bar is exactly four seconds at 60 BPM.</summary>
        private const double Bar = 4.0;

        /// <summary>Roots and voicings, a minor-sounding turn: Em, C(maj7), G, D(sus). The raid bed is the
        /// same four chords an octave down and a touch slower, because a bunker should not sound hopeful.</summary>
        private static readonly int[] MenuRoots = { 52, 48, 55, 50 };
        private static readonly int[] RaidRoots = { 40, 36, 43, 38 };
        private static readonly int[][] Shapes =
        {
            new[] { 0, 7, 12, 16 },
            new[] { 0, 7, 11, 16 },
            new[] { 0, 7, 12, 19 },
            new[] { 0, 7, 14, 19 },
        };

        /// <summary>
        /// Post-rock as arithmetic: clean tremolo-picked chords that swell in and out once a bar, a sub under
        /// the root, and three delay taps whose tails wrap around the end of the loop - so the seam is a
        /// musical join instead of a cut, and nothing has to be faded.
        /// </summary>
        private static float[] PostRock(bool raid)
        {
            var roots = raid ? RaidRoots : MenuRoots;
            var tremolo = raid ? 5.5 : 7.0;
            var length = (int)Math.Round(LoopSeconds * SampleRate);
            var samples = new double[length];

            for (var i = 0; i < length; i++)
            {
                var t = (double)i / SampleRate;
                var inBar = t % Bar;
                var bar = (int)(t / Bar) % 4;

                // A breath per bar: nothing at the bar line, widest in the middle, so every loop starts from
                // silence and the swells never fight each other.
                var swell = Math.Sin(Math.PI * inBar / Bar);
                var pick = 0.62 + 0.38 * Math.Sin(TwoPi * tremolo * t);

                var value = 0.0;
                foreach (var interval in Shapes[bar])
                {
                    var frequency = Midi(roots[bar] + interval);
                    // Two voices a shade apart: that detune is the whole "wide guitar" effect.
                    value += Guitar(frequency, t) * 0.55;
                    value += Guitar(frequency * 1.0035, t) * 0.35;
                }

                var sub = Math.Sin(TwoPi * Midi(roots[bar] - 12) * t) * 0.16;
                var pulse = raid && inBar < 0.9
                    ? Math.Sin(TwoPi * 46 * inBar) * Decay(inBar, 7) * 0.12
                    : 0.0;
                var staticNoise = raid ? Static(t, i, 5) : 0.0;

                samples[i] = (value / Shapes[bar].Length) * swell * pick * 0.55 + sub * swell + pulse + staticNoise;
            }

            // The repeats: each tap is a copy of the whole loop, wrapped, so a tail that would have run past
            // the end comes back in at the start.
            var dry = (double[])samples.Clone();
            var delays = raid ? new[] { 0.55, 0.86, 1.29 } : new[] { 0.42, 0.63, 0.94 };
            var gains = new[] { 0.30, 0.19, 0.11 };
            for (var tap = 0; tap < delays.Length; tap++)
            {
                var shift = (int)(delays[tap] * SampleRate);
                for (var i = 0; i < length; i++)
                {
                    samples[i] += dry[(i - shift + length) % length] * gains[tap];
                }
            }

            // Peak-normalise: the sum of four voices plus three taps is not predictable, and a bed that
            // clips is worse than one that is too quiet.
            var peak = 0.0;
            foreach (var sample in samples)
            {
                peak = Math.Max(peak, Math.Abs(sample));
            }

            var scale = peak <= 0 ? 0 : (raid ? 0.42 : 0.38) / peak;
            var result = new float[length];
            for (var i = 0; i < length; i++)
            {
                result[i] = (float)Math.Max(-1, Math.Min(1, samples[i] * scale));
            }

            return result;
        }

        /// <summary>One heartbeat thump: a low sine that swells fast and dies slow, silent outside its window.</summary>
        private static double Thump(double t, double frequency, double decay)
        {
            if (t < 0 || t > 0.6)
            {
                return 0;
            }

            var swell = Math.Min(1, t / 0.012);
            return Math.Sin(TwoPi * frequency * t) * swell * Decay(t, decay);
        }

        /// <summary>Five harmonics falling off like a plucked string, no pick noise: clean.</summary>
        private static double Guitar(double frequency, double t)
        {
            var value = 0.0;
            for (var harmonic = 1; harmonic <= 5; harmonic++)
            {
                value += Math.Sin(TwoPi * frequency * harmonic * t) / Math.Pow(harmonic, 1.6);
            }

            return value;
        }

        /// <summary>One bar per root, twice through: a I–vi–ii–V turnaround, which is what a dance band
        /// would play while the news reader clears his throat.</summary>
        private static readonly int[] BarBass = { 48, 45, 50, 43 };   // C3, A2, D3, G2
        private static readonly int[] BarChord = { 60, 57, 62, 55 };  // C4, A3, D4, G3
        private static readonly int[][] BarShape =
        {
            new[] { 0, 4, 7, 10 }, // Cmaj7
            new[] { 0, 3, 7, 10 }, // Am7
            new[] { 0, 3, 7, 10 }, // Dm7
            new[] { 0, 4, 7, 10 }, // G7
        };

        private const double Beat = 0.5; // 120 BPM: 32 beats fill the 16 second loop

        /// <summary>
        /// Walking bass on every beat, a soft comp on the first and third, brushes on the second and fourth,
        /// and a scratched record under it all. Every sound starts and finishes inside its beat, so the loop
        /// has nothing hanging over the seam.
        /// </summary>
        private static double RadioBed(double t, int index)
        {
            var beatIndex = (int)Math.Floor(t / Beat);
            var inBeat = t - beatIndex * Beat;
            var bar = beatIndex / 4 % 4;
            var step = beatIndex % 4;

            // Bass: root, fifth, third, then a semitone below the next bar's root to lead into it.
            var bass = step switch
            {
                0 => BarBass[bar],
                1 => BarBass[bar] + 7,
                2 => BarBass[bar] + (BarShape[bar][1] == 4 ? 4 : 3),
                _ => BarBass[(bar + 1) % 4] - 1,
            };
            var bassFreq = Midi(bass);
            var bassSound = (Math.Sin(TwoPi * bassFreq * t) + 0.35 * Math.Sin(TwoPi * 2 * bassFreq * t))
                            * Decay(inBeat, 5.5) * 0.20;

            // Comp: the triad, quietly, on beats one and three.
            var comp = 0.0;
            if (step == 0 || step == 2)
            {
                foreach (var interval in BarShape[bar])
                {
                    comp += Math.Sin(TwoPi * Midi(BarChord[bar] + interval) * t) * Decay(inBeat, 4.2) * 0.030;
                }
            }

            // Brushes on two and four: the difference between neighbouring noise samples is a cheap high pass,
            // which is what a brush on a snare sounds like from across a room.
            var brush = step == 1 || step == 3
                ? (Noise(index, 61) - Noise(index - 1, 61)) * Decay(inBeat, 34) * 0.09
                : 0.0;

            // A record: hiss, and the occasional scratch that lands wherever it lands.
            var hiss = Noise(index, 93) * 0.010;
            var scratch = Noise(index, 91) > 0.9993 ? 0.30 : 0.0;

            return bassSound + comp + brush + hiss + scratch;
        }

        /// <summary>
        /// The wireless itself: a one-pole low pass and a matching high pass, which is most of what an AM
        /// radio did to a band. Run twice over the loop so the filter is already warm at the seam instead of
        /// fading the first few milliseconds in.
        /// </summary>
        private static float[] Radio(float[] samples)
        {
            var source = (float[])samples.Clone();
            var low = 0.0;
            var previousIn = 0.0;
            var previousOut = 0.0;
            for (var pass = 0; pass < 2; pass++)
            {
                for (var i = 0; i < source.Length; i++)
                {
                    low += 0.30 * (source[i] - low);
                    var high = low - previousIn + 0.985 * previousOut;
                    previousIn = low;
                    previousOut = high;
                    if (pass == 1)
                    {
                        var value = high * 1.6;
                        samples[i] = value > 1 ? 1f : value < -1 ? -1f : (float)value;
                    }
                }
            }

            return samples;
        }

        /// <summary>Equal-tempered pitch of a MIDI note number.</summary>
        private static double Midi(int note) => 440.0 * Math.Pow(2, (note - 69) / 12.0);

        /// <summary>
        /// One key press: a metallic tick with a short body and a thump under it. Negative time is silence,
        /// so a clip can be a rhythm of presses by adding shifted copies.
        /// </summary>
        private static double Key(double t, int index, double resonance)
        {
            if (t < 0 || t > 0.13)
            {
                return 0;
            }

            return Math.Sin(TwoPi * resonance * t) * Decay(t, 90) * 0.55
                   + Noise(index, 5) * Decay(t, 150) * 0.40
                   + Math.Sin(TwoPi * 190 * t) * Decay(t, 55) * 0.30;
        }

        /// <summary>Faint hiss from an open channel, drifting a whole number of times per loop.</summary>
        private static double Static(double t, int index, int cycles)
        {
            var drift = 0.5 + 0.5 * Math.Sin(TwoPi * cycles * t / LoopSeconds);
            return Noise(index, 13) * 0.022 * drift;
        }

        /// <summary>Distant artillery: four thumps a loop, all of them finished before the seam.</summary>
        private static double Thumps(double t, int index)
        {
            var sum = 0.0;
            foreach (var at in new[] { 2.5, 6.0, 9.75, 13.0 })
            {
                var local = t - at;
                if (local < 0 || local > 1.4)
                {
                    continue;
                }

                sum += Math.Sin(TwoPi * 37 * local) * Decay(local, 3.2) * 0.20
                       + Noise(index, 17) * Decay(local, 6) * 0.05;
            }

            return sum;
        }

        /// <summary>
        /// A bed of four low partials with slow amplitude drifts, quiet enough to sit under everything.
        /// The drift rates are whole cycles per loop (1, 2, 3, 1) - that is what keeps the seam silent.
        /// </summary>
        private static double Bed(double t, int index, double first, double second, double third, double fourth,
            double level, int seed)
        {
            var drift = TwoPi * t / LoopSeconds;
            var a = Math.Sin(TwoPi * first * t) * (0.78 + 0.22 * Math.Sin(drift));
            var b = Math.Sin(TwoPi * second * t) * (0.70 + 0.30 * Math.Sin(2 * drift + 1.1));
            var c = Math.Sin(TwoPi * third * t) * (0.60 + 0.40 * Math.Sin(3 * drift + 2.3));
            var d = Math.Sin(TwoPi * fourth * t) * (0.55 + 0.45 * Math.Sin(drift + 4.0));
            var breath = Noise(index, seed) * 0.02 * (0.5 + 0.5 * Math.Sin(2 * drift));
            return (a + b + c + d) / 4.0 * level + breath;
        }

        private static float[] Render(double seconds, Func<double, int, double> wave)
        {
            var samples = new float[(int)Math.Round(seconds * SampleRate)];
            for (var i = 0; i < samples.Length; i++)
            {
                var value = wave((double)i / SampleRate, i);
                samples[i] = value > 1 ? 1f : value < -1 ? -1f : (float)value;
            }

            return samples;
        }

        /// <summary>A sine whose pitch slides from one frequency to another over the whole clip.</summary>
        private static double Tone(double from, double to, double t, double seconds = 0.12)
        {
            var span = seconds * 0.5;
            var phase = TwoPi * (from * t + (to - from) * t * t / (2 * Math.Max(span, 1e-6)));
            return Math.Sin(phase);
        }

        private static double Decay(double t, double rate) => Math.Exp(-rate * t);

        private static double Envelope(double t, double attack, double length)
        {
            if (t < 0 || t > length)
            {
                return 0;
            }

            var rise = attack <= 0 ? 1 : Math.Min(1, t / attack);
            var fall = Math.Min(1, (length - t) / Math.Max(0.02, length * 0.5));
            return rise * fall;
        }

        /// <summary>Deterministic white noise: same build, same sound, and no Random object to seed.</summary>
        private static double Noise(int index, int seed)
        {
            var x = (uint)(index * 747796405 + seed * 2891336453);
            x = (x >> 13) ^ x;
            var hash = (x * (x * x * 15731u + 789221u) + 1376312589u) & 0x7fffffffu;
            return 1.0 - hash / 1073741824.0;
        }
    }
}
