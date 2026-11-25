using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Safehouse.UI
{
    /// <summary>
    /// One-shot effects the game can ask for by name. The name in lower case is also the file name
    /// <see cref="AudioDirector"/> looks for (<c>StreamingAssets/audio/click.wav</c>) before it falls back
    /// to <see cref="ToneBank"/>.
    /// </summary>
    public enum Sound
    {
        Click,
        Error,
        Move,
        Equip,
        Trash,
        Case,
        Heal,

        /// <summary>Money changed hands: a buy or a sell that actually went through.</summary>
        Trade,

        Shot,

        /// <summary>A looping pulse, on while somebody is one hit from losing a part. Not a one-shot: it is
        /// a state, so it starts when the state does and stops when it ends.</summary>
        Heartbeat,

        Death,
        Extract,

        /// <summary>The checkpoint radio: the sound a military scene opens with.</summary>
        Radio,
    }

    /// <summary>
    /// The game's ears: one music bed at a time (with a fade between beds), and one source for effects.
    /// Clips come from <c>StreamingAssets/audio/&lt;id&gt;.wav</c> when such a file exists, and from
    /// <see cref="ToneBank"/> when it does not - so the game is audible with no audio assets at all, and
    /// a real recording can be dropped in later without touching any code.
    ///
    /// It builds itself before the first scene (<see cref="RuntimeInitializeOnLoadMethod"/>), so no scene
    /// has to be re-authored to get sound, and it survives screen changes. Callers only use the static
    /// methods: with no director (batch mode, tests, no audio device) every call is silence.
    /// </summary>
    [DefaultExecutionOrder(-400)]
    public sealed class AudioDirector : MonoBehaviour
    {
        public const string MusicFolderName = "audio";
        private const string MusicKey = "Safehouse.MusicVolume.v2";
        private const string SfxKey = "Safehouse.SfxVolume";
        private const float FadePerSecond = 0.9f;

        private static AudioDirector _instance;

        private readonly Dictionary<string, AudioClip> _clips = new Dictionary<string, AudioClip>(StringComparer.Ordinal);
        private string _folder;
        private AudioSource _bedA;
        private AudioSource _bedB;
        private AudioSource _sfx;
        private AudioSource _heart;
        private AudioSource _ambience;
        private bool _bedAIsLive = true;
        private bool _listenerAdded;
        private string _musicId;      // what should be playing; null is silence
        private string _pendingMusic; // where to switch to, once the live bed has faded out
        private bool _switching;
        private float _fadeA;
        private float _fadeB;

        /// <summary>The live director, or null when audio never started (batch mode, a test, no device).</summary>
        public static AudioDirector Instance => _instance;

        // ---- volumes (persisted, so a setting survives a restart) ----

        /// <summary>
        /// The one music level in the game: a single static property, read by the two bed sources every frame
        /// and written by the SETTINGS slider, persisted under a versioned key. Versioned because the level
        /// has now been decided three times (0.7, 0.55, 0.4) and an old stored value would otherwise shadow
        /// the new default forever - a global setting has to be one number, in one place, that can be changed.
        /// </summary>
        public static float MusicVolume
        {
            get => Mathf.Clamp01(PlayerPrefs.GetFloat(MusicKey, MusicDefault));
            set => PlayerPrefs.SetFloat(MusicKey, Mathf.Clamp01(value));
        }

        private const float MusicDefault = 0.4f;

        public static float SfxVolume
        {
            get => Mathf.Clamp01(PlayerPrefs.GetFloat(SfxKey, 0.7f));
            set => PlayerPrefs.SetFloat(SfxKey, Mathf.Clamp01(value));
        }

        // ---- the static face the screens use ----

        /// <summary>Plays one effect. Unknown, missing or unavailable is silence, never an exception.</summary>
        public static void Play(Sound sound, float volume = 1f)
        {
            // One lookup, no allocation: every screen calls this on its own actions.
            if (_instance != null)
            {
                _instance.PlayEffect(sound.ToString().ToLowerInvariant(), volume);
            }
        }

        /// <summary>
        /// Switches the bed by id ("menu", "raid"), fading the old one out first. Asking for the bed that is
        /// already playing does nothing, so every screen can ask on show without restarting the music.
        /// </summary>
        public static void Music(string id)
        {
            var director = _instance;
            if (director == null || id == director._musicId && !director._switching)
            {
                return;
            }

            director._pendingMusic = id;
            director._switching = true;
        }

        /// <summary>Fades the bed out (a trip that ended badly does not keep its music).</summary>
        public static void Silence() => Music(null);

        /// <summary>
        /// The heartbeat, which loops rather than fires: on while somebody in the party is about to lose a
        /// part, off the moment that is no longer true (healed, extracted, dead). Calling it with the state it
        /// is already in does nothing, so a screen can just ask every time it redraws.
        /// </summary>
        public static void Heartbeat(bool on) => _instance?.SetHeartbeat(on);

        private void SetHeartbeat(bool on)
        {
            if (_heart == null)
            {
                return;
            }

            if (!on)
            {
                if (_heart.isPlaying)
                {
                    _heart.Stop();
                }

                return;
            }

            if (_heart.isPlaying)
            {
                return;
            }

            var clip = Clip("heartbeat");
            if (clip == null)
            {
                return;
            }

            _heart.clip = clip;
            _heart.volume = HeartbeatVolume;
            _heart.Play();
        }

        /// <summary>
        /// The radio bed under a scene: a long recording, looped, on its own source. It runs beside the music
        /// instead of replacing it - the score is the room, the radio is what is in it - and it never touches
        /// the music volume, so navigaton keeps whatever bed it set.
        /// </summary>
        public static void Ambience(bool on) => _instance?.SetAmbience(on);

        private void SetAmbience(bool on)
        {
            if (_ambience == null)
            {
                return;
            }

            if (!on)
            {
                if (_ambience.isPlaying)
                {
                    _ambience.Stop();
                }

                return;
            }

            if (_ambience.isPlaying)
            {
                return;
            }

            var clip = Clip("radio");
            if (clip == null)
            {
                return;
            }

            _ambience.clip = clip;
            _ambience.volume = AmbienceVolume;
            _ambience.Play();
        }

        /// <summary>0.2 of the effect level: audible under the music, never louder than it.</summary>
        private static float AmbienceVolume => Mathf.Clamp01(SfxVolume) * 0.2f;

        private static float HeartbeatVolume => Mathf.Clamp01(SfxVolume) * 0.9f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Boot()
        {
            if (_instance != null || Application.isBatchMode)
            {
                return;
            }

            var host = new GameObject("Audio");
            DontDestroyOnLoad(host);
            _instance = host.AddComponent<AudioDirector>();
        }

        private void Awake()
        {
            _instance = this;
            _folder = Path.Combine(Application.streamingAssetsPath, MusicFolderName);

            // The scene has a camera but no AudioListener, which makes Unity warn on every frame that
            // something plays. The audio host carries one instead - and only when the scene has none, so a
            // camera that does listen is left alone (two listeners is a warning of its own).
            if (FindFirstObjectByType<AudioListener>() == null)
            {
                gameObject.AddComponent<AudioListener>();
                _listenerAdded = true;
            }

            _bedA = gameObject.AddComponent<AudioSource>();
            _bedB = gameObject.AddComponent<AudioSource>();
            foreach (var bed in new[] { _bedA, _bedB })
            {
                bed.loop = true;
                bed.playOnAwake = false;
                bed.volume = 0f;
                bed.spatialBlend = 0f; // 2D: nothing in this game's audio is positioned
            }

            _sfx = gameObject.AddComponent<AudioSource>();
            _sfx.playOnAwake = false;
            _sfx.spatialBlend = 0f;

            _heart = gameObject.AddComponent<AudioSource>();
            _heart.loop = true;
            _heart.playOnAwake = false;
            _heart.spatialBlend = 0f;
            _heart.volume = 0f;

            _ambience = gameObject.AddComponent<AudioSource>();
            _ambience.loop = true;
            _ambience.playOnAwake = false;
            _ambience.spatialBlend = 0f;
            _ambience.volume = 0f;
        }

        private void Start()
        {
            // The scene has finished loading by now: if it brought a listener of its own, ours is the spare
            // one and has to go, or Unity complains about two.
            if (_listenerAdded && FindObjectsByType<AudioListener>(FindObjectsSortMode.None).Length > 1)
            {
                Destroy(GetComponent<AudioListener>());
                _listenerAdded = false;
            }
        }

        private void Update()
        {
            var step = FadePerSecond * Time.unscaledDeltaTime;

            if (_switching)
            {
                // The old bed goes all the way out before the new one starts: two beds at once sounds like
                // a mistake, and this game only ever wants one.
                _fadeA = Mathf.MoveTowards(_fadeA, 0f, step);
                _fadeB = Mathf.MoveTowards(_fadeB, 0f, step);
                if (_fadeA <= 0f && _fadeB <= 0f)
                {
                    var next = _pendingMusic;
                    _switching = false;
                    StartBed(next);
                }
            }
            else if (_musicId != null)
            {
                if (_bedAIsLive)
                {
                    _fadeA = Mathf.MoveTowards(_fadeA, 1f, step);
                }
                else
                {
                    _fadeB = Mathf.MoveTowards(_fadeB, 1f, step);
                }
            }
            else
            {
                _fadeA = Mathf.MoveTowards(_fadeA, 0f, step);
                _fadeB = Mathf.MoveTowards(_fadeB, 0f, step);
            }

            ApplyMusicVolumes();
            if (_heart != null && _heart.isPlaying)
            {
                _heart.volume = HeartbeatVolume; // follows the SFX slider while it plays
            }
        }

        private void StartBed(string id)
        {
            _musicId = id;
            if (id == null)
            {
                return;
            }

            var clip = Clip(id);
            var source = _bedAIsLive ? _bedB : _bedA;
            if (clip == null)
            {
                _musicId = null;
                return;
            }

            source.clip = clip;
            source.volume = 0f;
            source.Play();
            _bedAIsLive = !_bedAIsLive;
            if (_bedAIsLive)
            {
                _fadeA = 0f;
            }
            else
            {
                _fadeB = 0f;
            }
        }

        private void ApplyMusicVolumes()
        {
            var level = MusicVolume;
            _bedA.volume = _fadeA * level;
            _bedB.volume = _fadeB * level;
            if (_musicId == null && !_switching && _fadeA <= 0f && _fadeB <= 0f)
            {
                _bedA.Stop();
                _bedB.Stop();
            }
        }

        private void PlayEffect(string id, float volume)
        {
            var clip = Clip(id);
            if (clip == null || volume <= 0f || _sfx == null)
            {
                return;
            }

            _sfx.PlayOneShot(clip, Mathf.Clamp01(volume) * SfxVolume);
        }

        /// <summary>
        /// A clip by id: the .wav next to the game's data files when there is one, otherwise the synth.
        /// Cached, so a click played a thousand times is read and built once - misses included, so nothing
        /// keeps hitting the disk or warning about the same missing file.
        /// </summary>
        private AudioClip Clip(string id)
        {
            if (id == null)
            {
                return null;
            }

            if (_clips.TryGetValue(id, out var cached))
            {
                return cached;
            }

            var clip = FromFile(id) ?? FromBank(id);
            _clips[id] = clip;
            return clip;
        }

        private AudioClip FromFile(string id)
        {
            var path = Path.Combine(_folder, id + ".wav");
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                var (samples, rate) = WaveFile.Parse(File.ReadAllBytes(path));
                return ToClip(id, samples, rate);
            }
            catch (Exception error)
            {
                // A broken file must not take the game down with it: the synth answers instead.
                Debug.LogWarning($"Audio file {path} was not usable ({error.Message}); using the built-in sound.");
                return null;
            }
        }

        private static AudioClip FromBank(string id)
        {
            var samples = ToneBank.Build(id);
            return samples == null ? null : ToClip(id, samples, ToneBank.SampleRate);
        }

        private static AudioClip ToClip(string id, float[] samples, int rate)
        {
            if (samples == null || samples.Length == 0)
            {
                return null;
            }

            var clip = AudioClip.Create(id, samples.Length, 1, rate, false);
            clip.SetData(samples, 0);
            return clip;
        }
    }
}
