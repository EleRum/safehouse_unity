using System;
using System.Diagnostics;
using Safehouse.Data;
using UnityEngine;
using UnityEngine.UIElements;

namespace Safehouse.UI
{
    /// <summary>
    /// The SETTINGS screen: shows where the game keeps its data and exposes useful shortcuts.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class SettingsScreenController : MonoBehaviour
    {
        private VisualElement _root;
        private Label _labelDataFolder;
        private Button _buttonOpenFolder;
        private Label _labelStatus;
        private Slider _music;
        private Slider _sfx;
        private Label _labelMusic;
        private Label _labelSfx;

        private void OnEnable()
        {
            _root = GetComponent<UIDocument>().rootVisualElement;
            _root.style.alignItems = Align.Center;
            _root.style.justifyContent = Justify.Center;

            _labelDataFolder = _root.Q<Label>("label-data-folder");
            _buttonOpenFolder = _root.Q<Button>("button-open-folder");
            _labelStatus = _root.Q<Label>("label-status");
            BindAudio();

            _root.Q<Button>("navtab-character").clicked += () => ScreenNavigator.Go("character");
            _root.Q<Button>("navtab-gear").clicked += () => ScreenNavigator.Go("gear");
            _root.Q<Button>("navtab-traders").clicked += () => ScreenNavigator.Go("traders");
            _root.Q<Button>("navtab-hideout").clicked += () => ScreenNavigator.Go("hideout");
            _root.Q<Button>("navtab-scavenge").clicked += () => ScreenNavigator.Go("scavenge");
            var gmTab = _root.Q<Button>("navtab-gm");
            if (gmTab != null)
            {
                gmTab.clicked += () => ScreenNavigator.Go("gm");
            }
            _root.Q<Button>("navtab-quests").clicked += () => ScreenNavigator.Go("quests");
            _buttonOpenFolder.clicked += OpenDataFolder;

            ScreenNavigator.Register("settings", Show, Hide);
            Hide();
        }

        private void OnDisable()
        {
            ScreenNavigator.Unregister("settings");
        }

        public void Show()
        {
            _root.style.display = DisplayStyle.Flex;
            Refresh();
        }

        public void Hide()
        {
            _root.style.display = DisplayStyle.None;
        }

        private void Refresh()
        {
            var path = ProfileRepository.DefaultFolder;
            _labelDataFolder.text = path;
            _labelStatus.text = "";
            ShowVolumes();
        }

        /// <summary>
        /// The two volume sliders and the button that proves the speakers are on. Both values live in
        /// PlayerPrefs inside <see cref="AudioDirector"/>, so nothing here has to remember them.
        /// </summary>
        private void BindAudio()
        {
            _music = _root.Q<Slider>("slider-music");
            _sfx = _root.Q<Slider>("slider-sfx");
            _labelMusic = _root.Q<Label>("label-music");
            _labelSfx = _root.Q<Label>("label-sfx");

            if (_music != null)
            {
                _music.SetValueWithoutNotify(AudioDirector.MusicVolume * 100f);
                _music.RegisterValueChangedCallback(changed =>
                {
                    AudioDirector.MusicVolume = changed.newValue / 100f;
                    ShowVolumes();
                });
            }

            if (_sfx != null)
            {
                _sfx.SetValueWithoutNotify(AudioDirector.SfxVolume * 100f);
                _sfx.RegisterValueChangedCallback(changed =>
                {
                    AudioDirector.SfxVolume = changed.newValue / 100f;
                    ShowVolumes();
                    AudioDirector.Play(Sound.Case); // so the slider can be heard, not only seen
                });
            }

            var test = _root.Q<Button>("button-test-sound");
            if (test != null)
            {
                test.clicked += () => AudioDirector.Play(Sound.Case);
            }
        }

        private void ShowVolumes()
        {
            if (_labelMusic != null)
            {
                _labelMusic.text = Mathf.RoundToInt(AudioDirector.MusicVolume * 100f).ToString();
            }

            if (_labelSfx != null)
            {
                _labelSfx.text = Mathf.RoundToInt(AudioDirector.SfxVolume * 100f).ToString();
            }
        }

        private void OpenDataFolder()
        {
            var path = ProfileRepository.DefaultFolder;
            try
            {
                if (Application.platform == RuntimePlatform.WindowsPlayer || Application.platform == RuntimePlatform.WindowsEditor)
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = false });
                }
                else
                {
                    Application.OpenURL($"file://{path}");
                }

                _labelStatus.text = "Opened the data folder.";
            }
            catch (Exception error)
            {
                _labelStatus.text = $"Could not open folder: {error.Message}";
                _labelStatus.EnableInClassList("text-danger", true);
            }
        }
    }
}
