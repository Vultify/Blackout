using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

namespace Blackout
{
    [BepInPlugin("com.vultify.blackout", "Blackout", "1.0.0")]
    public class BlackoutPlugin : BaseUnityPlugin
    {
        private const string LabsLocationId = "laboratory";

        private const float RescanIntervalSec = 2f;

        public enum SoundMethod
        {
            GuiSounds,
            SceneMixerClone,
            AtPointEnvironment,
            Nonspatial,
            NonspatialBypass,
            RawAudioSource2D,
            RawAudioSource3D
        }

        private ConfigEntry<bool> _modEnabled;
        private ConfigEntry<bool> _labsOnly;
        private ConfigEntry<float> _delaySeconds;
        private ConfigEntry<float> _darknessStrength;
        private ConfigEntry<float> _flashlightGain;
        private ConfigEntry<float> _emergencyDim;
        private ConfigEntry<float> _soundVolume;
        private ConfigEntry<SoundMethod> _soundMethod;
        private ConfigEntry<bool> _announcementEnabled;
        private ConfigEntry<float> _announcementDelay;
        private ConfigEntry<bool> _subtitleEnabled;
        private ConfigEntry<string> _subtitleTextCfg;
        private ConfigEntry<KeyboardShortcut> _testSoundKey;
        private ConfigEntry<KeyboardShortcut> _dumpLightsKey;
        private ConfigEntry<KeyboardShortcut> _knownClipKey;

        private bool _inRaid;
        private bool _clockStarted;
        private bool _blackoutActive;
        private float _blackoutAt;
        private float _nextRescan;
        private float _announcementAt;
        private bool _announcementPlayed;
        private AudioClip _powerDownClip;
        private AudioClip _announcerClip;
        private string _subtitleText;
        private float _subtitleUntil;
        private Texture2D _subtitleBg;
        private Texture2D _subtitleFrame;
        private GUIStyle _subtitleStyle;
        private bool _errorLogged;

        private readonly List<LightState> _killedLights = new List<LightState>();
        private readonly HashSet<Light> _trackedLights = new HashSet<Light>();
        private readonly Dictionary<Light, GearLightState> _gearLights = new Dictionary<Light, GearLightState>();
        private PostProcessVolume _ppVolume;
        private ColorGrading _colorGrading;
        private LightmapData[] _originalLightmaps;
        private float _originalAmbientIntensity;
        private Color _originalAmbientLight;
        private float _originalReflectionIntensity;

        private struct LightState
        {
            public Light Light;
            public float Intensity;
        }

        private class GearLightState
        {
            public float Baseline;
            public float LastSet;
        }

        private void Awake()
        {
            _modEnabled = Config.Bind(
                "1. General",
                "Enable Mod",
                true,
                "Master toggle — enables or disables the entire mod");

            _labsOnly = Config.Bind(
                "1. General",
                "Labs Only",
                true,
                "Only trigger the blackout on The Lab (like the live event). Disable to black out every map");

            _delaySeconds = Config.Bind(
                "2. Blackout",
                "Delay",
                15f,
                new ConfigDescription(
                    "Seconds after you gain control before the power goes out",
                    new AcceptableValueRange<float>(0f, 120f)));

            _darknessStrength = Config.Bind(
                "2. Blackout",
                "Darkness Strength",
                1f,
                new ConfigDescription(
                    "1 = pitch black, lower values leave residual ambient light. Adjusts live",
                    new AcceptableValueRange<float>(0.1f, 1f)));

            _emergencyDim = Config.Bind(
                "2. Blackout",
                "Emergency Power Dim",
                0.3f,
                new ConfigDescription(
                    "Dimming from raid start before the full cut, like the live event's emergency power. 0 = normal lighting until the cut",
                    new AcceptableValueRange<float>(0f, 0.8f)));

            _flashlightGain = Config.Bind(
                "2. Blackout",
                "Flashlight Gain",
                8f,
                new ConfigDescription(
                    "Extra brightness for player and bot gear lights on top of darkness compensation. 1 = same as a lit room",
                    new AcceptableValueRange<float>(1f, 32f)));

            _soundVolume = Config.Bind(
                "3. Sound",
                "Volume",
                0.8f,
                new ConfigDescription(
                    "Volume of the generator power-down sound",
                    new AcceptableValueRange<float>(0f, 1f)));

            _soundMethod = Config.Bind(
                "3. Sound",
                "Playback Method",
                SoundMethod.GuiSounds,
                "How the sound is routed into the game's audio. If you can't hear it, try another method and press the test key");

            _announcementEnabled = Config.Bind(
                "3. Sound",
                "Intercom Announcement",
                true,
                "Play the event's Announcement System voice line after the power goes out");

            _announcementDelay = Config.Bind(
                "3. Sound",
                "Announcement Delay",
                2f,
                new ConfigDescription(
                    "Seconds after the blackout before the intercom announcement plays",
                    new AcceptableValueRange<float>(0f, 60f)));

            _subtitleEnabled = Config.Bind(
                "3. Sound",
                "Announcement Subtitle",
                true,
                "Show the Announcement System text box on screen while the intercom voice plays");

            _subtitleTextCfg = Config.Bind(
                "3. Sound",
                "Subtitle Text",
                "The facility has been switched to emergency power. Please remain where you are and await evacuation.",
                "Text shown in the Announcement System box while the intercom voice plays");

            _testSoundKey = Config.Bind(
                "4. Debug",
                "Test Sound Key",
                new KeyboardShortcut(KeyCode.F10),
                "Plays the power-down sound in-raid using the selected playback method, for testing without restarting");

            _dumpLightsKey = Config.Bind(
                "4. Debug",
                "Dump Lights Key",
                new KeyboardShortcut(KeyCode.F11),
                "Logs every enabled light in the scene (turn your flashlight on first) so flashlight handling can be debugged");

            _knownClipKey = Config.Bind(
                "4. Debug",
                "Known Clip Test Key",
                new KeyboardShortcut(KeyCode.F9),
                "Plays a clip the game itself loaded, via the selected playback method - tells apart broken routing from broken files");

            LoadSoundBundle();

            // the game re-asserts tactical light intensity after LateUpdate, this hook runs last
            Application.onBeforeRender += OnBeforeRender;
        }

        private void OnDestroy()
        {
            Application.onBeforeRender -= OnBeforeRender;
        }

        private void OnBeforeRender()
        {
            if (!_blackoutActive)
            {
                return;
            }
            try
            {
                ApplyGearBoost();
            }
            catch
            {
                // Update's error path already reports; keep the render-phase hook quiet
            }
        }

        private void HandleDebugKeys()
        {
            if (_testSoundKey.Value.IsDown())
            {
                Logger.LogInfo("[Blackout] Test announcement via intercom path");
                PlayAnnouncement();
            }

            if (_knownClipKey.Value.IsDown())
            {
                AudioClip donor = null;
                foreach (var s in FindObjectsOfType<AudioSource>())
                {
                    if (s != null && s.clip != null)
                    {
                        donor = s.clip;
                        if (s.isPlaying)
                        {
                            break;
                        }
                    }
                }
                if (donor == null)
                {
                    Logger.LogWarning("[Blackout] No scene AudioSource with a clip found for the known-clip test");
                }
                else
                {
                    Logger.LogInfo($"[Blackout] Known-clip test: '{donor.name}' ({donor.length:0.0}s) via {_soundMethod.Value}");
                    PlayClip(donor);
                }
            }

            if (_dumpLightsKey.Value.IsDown())
            {
                DumpLights();
            }
        }

        private static bool IsGearLight(Light light)
        {
            if (light.name.ToLowerInvariant().Contains("muzzle"))
            {
                return false;
            }
            // the flashlight controller marks every tactical device light, pooled or not
            if (light.GetComponentInParent<TacticalComboVisualController>() != null)
            {
                return true;
            }
            for (var t = light.transform; t != null; t = t.parent)
            {
                if (t.name.StartsWith("nvg_") || t.name.StartsWith("flashlight_"))
                {
                    return true;
                }
            }
            return light.GetComponentInParent<Player>() != null;
        }

        private void ApplyGearBoost()
        {
            // if the game touched the intensity since we last set it (flashlight toggled),
            // treat the new value as the real baseline instead of clamping to a stale one
            var boost = Mathf.Pow(2f, 7f * _darknessStrength.Value) * _flashlightGain.Value;
            foreach (var pair in _gearLights)
            {
                var light = pair.Key;
                var state = pair.Value;
                if (light == null)
                {
                    continue;
                }
                var current = light.intensity;
                if (Mathf.Abs(current - state.LastSet) > 0.001f)
                {
                    state.Baseline = current;
                }
                light.intensity = state.Baseline * boost;
                state.LastSet = light.intensity;
            }
        }

        private void DumpLights()
        {
            var all = FindObjectsOfType<Light>();
            Logger.LogInfo($"[Blackout] === LIGHT DUMP: {all.Length} lights, {_gearLights.Count} tracked as gear, {_killedLights.Count} killed ===");
            foreach (var light in all)
            {
                if (light == null || !light.enabled)
                {
                    continue;
                }
                var path = light.name;
                var t = light.transform.parent;
                for (var i = 0; i < 8 && t != null; i++, t = t.parent)
                {
                    path = t.name + "/" + path;
                }
                var underPlayer = light.GetComponentInParent<Player>() != null;
                var gearInfo = _gearLights.TryGetValue(light, out var gs)
                    ? $"gear baseline={gs.Baseline:0.##} lastSet={gs.LastSet:0.##}"
                    : "notGear";
                Logger.LogInfo($"[Blackout]   ENABLED {light.type} intensity={light.intensity:0.##} range={light.range:0.#} underPlayer={underPlayer} {gearInfo} :: {path}");
            }
            Logger.LogInfo("[Blackout] === END LIGHT DUMP ===");
        }

        private void Update()
        {
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                // log the first failure instead of swallowing silently, then stay quiet
                if (!_errorLogged)
                {
                    _errorLogged = true;
                    Logger.LogError($"[Blackout] {ex}");
                }
            }
        }

        private void Tick()
        {
            HandleDebugKeys();

            var gameWorld = Singleton<GameWorld>.Instance;
            if (gameWorld == null || gameWorld.MainPlayer == null)
            {
                if (_inRaid)
                {
                    // raid ended; the scene (and everything we touched) is gone
                    _inRaid = false;
                    _clockStarted = false;
                    _blackoutActive = false;
                    _killedLights.Clear();
                    _trackedLights.Clear();
                    _gearLights.Clear();
                    _originalLightmaps = null;
                    _subtitleText = null;
                    DestroyPpVolume();
                }
                return;
            }

            _inRaid = true;

            if (!_modEnabled.Value)
            {
                if (_blackoutActive)
                {
                    RestoreEverything();
                }
                return;
            }

            if (_blackoutActive)
            {
                EnforceBlackout();
                if (!_announcementPlayed && _announcementEnabled.Value
                    && _announcerClip != null && Time.time >= _announcementAt)
                {
                    _announcementPlayed = true;
                    Logger.LogInfo("[Blackout] Announcement fired");
                    PlayAnnouncement();
                    if (_subtitleEnabled.Value && !string.IsNullOrWhiteSpace(_subtitleTextCfg.Value))
                    {
                        _subtitleText = _subtitleTextCfg.Value;
                        _subtitleUntil = Time.time + _announcerClip.length + 0.5f;
                    }
                }
                return;
            }

            if (_labsOnly.Value && gameWorld.LocationId != LabsLocationId)
            {
                return;
            }

            // don't start the clock until the player is actually in control -
            // MainPlayer exists long before the countdown ends
            if (!_clockStarted)
            {
                var game = Singleton<AbstractGame>.Instance;
                if (game == null || game.Status != GameStatus.Started)
                {
                    return;
                }
                _clockStarted = true;
                _blackoutAt = Time.time + _delaySeconds.Value;
                if (_emergencyDim.Value > 0f)
                {
                    CreatePpVolume();
                    Logger.LogInfo($"[Blackout] Emergency power dim ({_emergencyDim.Value:0.00})");
                }
                return;
            }

            if (Time.time < _blackoutAt)
            {
                if (_colorGrading != null)
                {
                    _colorGrading.postExposure.value = -7f * _emergencyDim.Value;
                }
                return;
            }

            ActivateBlackout();
        }

        private void ActivateBlackout()
        {
            _blackoutActive = true;
            _announcementPlayed = false;
            _announcementAt = Time.time + _announcementDelay.Value;

            _originalAmbientIntensity = RenderSettings.ambientIntensity;
            _originalAmbientLight = RenderSettings.ambientLight;
            _originalReflectionIntensity = RenderSettings.reflectionIntensity;

            // some maps carry baked lightmaps (Labs doesn't, confirmed 0 there)
            _originalLightmaps = LightmapSettings.lightmaps;
            LightmapSettings.lightmaps = new LightmapData[0];

            if (_ppVolume == null)
            {
                CreatePpVolume();
            }

            _nextRescan = 0f;
            EnforceBlackout();
            PlayPowerDownSound();
            Logger.LogInfo($"[Blackout] Power cut: {_killedLights.Count} lights killed, {_originalLightmaps.Length} lightmaps cleared, pp volume {(_ppVolume != null ? "on" : "OFF")}");
        }

        private void EnforceBlackout()
        {
            // scene scans are pricey, discover new lights on an interval,
            // but re-assert state on already-tracked ones every frame (ToD re-enables the sun)
            if (Time.time >= _nextRescan)
            {
                _nextRescan = Time.time + RescanIntervalSec;
                foreach (var light in FindObjectsOfType<Light>())
                {
                    if (light == null || _trackedLights.Contains(light))
                    {
                        continue;
                    }
                    _trackedLights.Add(light);

                    // player/bot gear lights (flashlights, lasers) get boosted instead of killed -
                    // they must punch through the exposure drop like they would on a real night raid
                    if (IsGearLight(light))
                    {
                        _gearLights[light] = new GearLightState { Baseline = light.intensity, LastSet = light.intensity };
                        continue;
                    }
                    _killedLights.Add(new LightState { Light = light, Intensity = light.intensity });
                }

                // weapons are pooled, a light discovered outside any player hierarchy can later
                // end up in someone's hands, re-check and resurrect those as gear
                for (var i = _killedLights.Count - 1; i >= 0; i--)
                {
                    var state = _killedLights[i];
                    if (state.Light == null)
                    {
                        _killedLights.RemoveAt(i);
                        continue;
                    }
                    if (!IsGearLight(state.Light))
                    {
                        continue;
                    }
                    _killedLights.RemoveAt(i);
                    state.Light.enabled = true;
                    state.Light.intensity = state.Intensity;
                    _gearLights[state.Light] = new GearLightState { Baseline = state.Intensity, LastSet = state.Intensity };
                }
            }

            foreach (var state in _killedLights)
            {
                if (state.Light != null && state.Light.enabled)
                {
                    state.Light.enabled = false;
                }
            }

            ApplyGearBoost();

            // live so the F12 slider is directly visible in-scene
            var residual = 1f - _darknessStrength.Value;
            RenderSettings.ambientIntensity = _originalAmbientIntensity * residual;
            RenderSettings.ambientLight = _originalAmbientLight * residual;
            RenderSettings.reflectionIntensity = _originalReflectionIntensity * residual;

            // EFT's shaders light Labs through their own channels, so image-space exposure
            // is the only lever guaranteed to reach night-raid darkness
            if (_colorGrading != null)
            {
                _colorGrading.postExposure.value = -7f * _darknessStrength.Value;
            }
        }

        private void CreatePpVolume()
        {
            var ppLayer = FindObjectOfType<PostProcessLayer>();
            if (ppLayer == null)
            {
                Logger.LogWarning("[Blackout] No PostProcessLayer found — exposure darkening unavailable");
                return;
            }

            var mask = ppLayer.volumeLayer.value;
            var layerIndex = 0;
            for (var i = 0; i < 32; i++)
            {
                if ((mask & (1 << i)) != 0)
                {
                    layerIndex = i;
                    break;
                }
            }

            _colorGrading = ScriptableObject.CreateInstance<ColorGrading>();
            _colorGrading.enabled.Override(true);
            _colorGrading.postExposure.Override(-7f * _darknessStrength.Value);
            _ppVolume = PostProcessManager.instance.QuickVolume(layerIndex, 1000f, _colorGrading);
        }

        private void DestroyPpVolume()
        {
            if (_ppVolume != null)
            {
                RuntimeUtilities.DestroyVolume(_ppVolume, true, true);
            }
            _ppVolume = null;
            _colorGrading = null;
        }

        private void RestoreEverything()
        {
            _blackoutActive = false;
            _clockStarted = false;

            foreach (var state in _killedLights)
            {
                if (state.Light != null)
                {
                    state.Light.enabled = true;
                    state.Light.intensity = state.Intensity;
                }
            }
            foreach (var pair in _gearLights)
            {
                if (pair.Key != null)
                {
                    pair.Key.intensity = pair.Value.Baseline;
                }
            }
            _killedLights.Clear();
            _trackedLights.Clear();
            _gearLights.Clear();

            if (_originalLightmaps != null)
            {
                LightmapSettings.lightmaps = _originalLightmaps;
                _originalLightmaps = null;
            }
            RenderSettings.ambientIntensity = _originalAmbientIntensity;
            RenderSettings.ambientLight = _originalAmbientLight;
            RenderSettings.reflectionIntensity = _originalReflectionIntensity;
            DestroyPpVolume();

            Logger.LogInfo("[Blackout] Power restored (mod disabled mid-raid)");
        }

        private void OnGUI()
        {
            if (_subtitleText == null || Time.time >= _subtitleUntil)
            {
                return;
            }

            if (_subtitleBg == null)
            {
                _subtitleBg = MakeTexture(new Color(0f, 0f, 0f, 0.85f));
                _subtitleFrame = MakeTexture(new Color(0.9f, 0.9f, 0.9f, 0.95f));
                _subtitleStyle = new GUIStyle
                {
                    richText = true,
                    wordWrap = true,
                    normal = { textColor = Color.white }
                };
                foreach (var font in Resources.FindObjectsOfTypeAll<Font>())
                {
                    if (font != null && font.name.ToLowerInvariant().Contains("bender"))
                    {
                        _subtitleStyle.font = font;
                        break;
                    }
                }
                Logger.LogInfo($"[Blackout] Subtitle font: {(_subtitleStyle.font != null ? _subtitleStyle.font.name : "default")}");
            }

            _subtitleStyle.fontSize = Mathf.RoundToInt(Screen.height / 62f);
            var pad = Mathf.RoundToInt(Screen.height / 160f);
            var width = Screen.width * 0.42f;
            var content = new GUIContent("<b>Announcement System:</b> " + _subtitleText);
            var height = _subtitleStyle.CalcHeight(content, width - pad * 2f) + pad * 2f;
            var x = (Screen.width - width) / 2f;
            var y = Screen.height * 0.85f - height;

            GUI.DrawTexture(new Rect(x - 2f, y - 2f, width + 4f, height + 4f), _subtitleFrame);
            GUI.DrawTexture(new Rect(x, y, width, height), _subtitleBg);
            GUI.Label(new Rect(x + pad, y + pad, width - pad * 2f, height - pad * 2f), content, _subtitleStyle);
        }

        private static Texture2D MakeTexture(Color color)
        {
            var tex = new Texture2D(1, 1);
            tex.SetPixel(0, 0, color);
            tex.Apply();
            return tex;
        }

        private void PlayPowerDownSound()
        {
            PlayClip(_powerDownClip);
        }

        // same call chain the labs switch intercom uses (WorldInteractiveObject.PlaySoundAtPoint)
        private void PlayAnnouncement()
        {
            if (_announcerClip == null || _soundVolume.Value <= 0f)
            {
                return;
            }

            var gameWorld = Singleton<GameWorld>.Instance;
            var player = gameWorld != null ? gameWorld.MainPlayer : null;
            if (player == null || !MonoBehaviourSingleton<BetterAudio>.Instantiated)
            {
                PlayClip(_announcerClip);
                return;
            }

            var forward = Camera.main != null ? Camera.main.transform.forward : Vector3.forward;
            forward.y = 0f;
            forward = forward.sqrMagnitude > 0.01f ? forward.normalized : Vector3.forward;
            var pos = player.Position + forward * 12f + Vector3.up * 3.5f;

            MonoBehaviourSingleton<BetterAudio>.Instance.PlayAtPoint(
                pos,
                _announcerClip,
                BetterAudio.AudioSourceGroupType.InteractiveObjects,
                60,
                _soundVolume.Value,
                EOcclusionTest.None,
                null,
                true,
                false);
        }

        private void PlayClip(AudioClip clip)
        {
            if (clip == null || _soundVolume.Value <= 0f)
            {
                return;
            }

            var betterAudio = Singleton<BetterAudio>.Instance;
            var gameWorld = Singleton<GameWorld>.Instance;
            var playerPos = gameWorld?.MainPlayer != null ? gameWorld.MainPlayer.Position : Vector3.zero;

            switch (_soundMethod.Value)
            {
                case SoundMethod.GuiSounds:
                    var guiSounds = Singleton<EFT.UI.GUISounds>.Instance;
                    if (guiSounds == null)
                    {
                        Logger.LogWarning("[Blackout] GUISounds unavailable, using raw 2D");
                        goto case SoundMethod.RawAudioSource2D;
                    }
                    guiSounds.PlaySound(clip, false, false, _soundVolume.Value);
                    break;

                case SoundMethod.SceneMixerClone:
                    UnityEngine.Audio.AudioMixerGroup group = null;
                    foreach (var src in FindObjectsOfType<AudioSource>())
                    {
                        if (src != null && src.enabled && src.outputAudioMixerGroup != null)
                        {
                            group = src.outputAudioMixerGroup;
                            if (src.isPlaying)
                            {
                                break; // a source that's actually playing is the best donor
                            }
                        }
                    }
                    if (group == null)
                    {
                        Logger.LogWarning("[Blackout] No scene AudioSource with a mixer group found, using raw 2D");
                        goto case SoundMethod.RawAudioSource2D;
                    }
                    Logger.LogInfo($"[Blackout] Playing via cloned mixer group '{group.name}'");
                    var cloneGo = new GameObject("BlackoutSound");
                    var cloneSrc = cloneGo.AddComponent<AudioSource>();
                    cloneSrc.outputAudioMixerGroup = group;
                    cloneSrc.spatialBlend = 0f;
                    cloneSrc.volume = _soundVolume.Value;
                    cloneSrc.clip = clip;
                    cloneSrc.Play();
                    Destroy(cloneGo, clip.length + 1f);
                    break;

                case SoundMethod.AtPointEnvironment:
                    if (betterAudio == null) goto case SoundMethod.RawAudioSource2D;
                    // same playback path the Labs switch/intercom clips use (WorldInteractiveObject.PlaySoundAtPoint);
                    // rolloff is on a distance-like scale, small values are inaudible a few meters out
                    betterAudio.PlayAtPoint(
                        playerPos,
                        clip,
                        10f,
                        BetterAudio.AudioSourceGroupType.Environment,
                        100,
                        _soundVolume.Value,
                        EOcclusionTest.None,
                        null,
                        false);
                    break;

                case SoundMethod.Nonspatial:
                    if (betterAudio == null) goto case SoundMethod.RawAudioSource2D;
                    betterAudio.PlayNonspatial(
                        clip,
                        BetterAudio.AudioSourceGroupType.Nonspatial,
                        0f,
                        _soundVolume.Value,
                        null);
                    break;

                case SoundMethod.NonspatialBypass:
                    if (betterAudio == null) goto case SoundMethod.RawAudioSource2D;
                    betterAudio.PlayNonspatial(
                        clip,
                        BetterAudio.AudioSourceGroupType.NonspatialBypass,
                        0f,
                        _soundVolume.Value,
                        null);
                    break;

                case SoundMethod.RawAudioSource2D:
                    PlayRaw(clip, playerPos, 0f);
                    break;

                case SoundMethod.RawAudioSource3D:
                    PlayRaw(clip, playerPos, 1f);
                    break;
            }
        }

        private void PlayRaw(AudioClip clip, Vector3 position, float spatialBlend)
        {
            var go = new GameObject("BlackoutSound");
            go.transform.position = position;
            var source = go.AddComponent<AudioSource>();
            source.spatialBlend = spatialBlend;
            source.maxDistance = 100f;
            source.volume = _soundVolume.Value;
            source.clip = clip;
            source.Play();
            Destroy(go, clip.length + 1f);
        }

        private void LoadSoundBundle()
        {
            var path = Path.Combine(
                Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "",
                "blackout_sounds.bundle");
            if (!File.Exists(path))
            {
                Logger.LogWarning($"[Blackout] Sound bundle missing, blackout will be silent: {path}");
                return;
            }

            var bundle = AssetBundle.LoadFromFile(path);
            if (bundle == null)
            {
                Logger.LogError("[Blackout] Failed to open sound bundle");
                return;
            }

            _powerDownClip = bundle.LoadAsset<AudioClip>("black_out_huge");
            _announcerClip = bundle.LoadAsset<AudioClip>("announcer_lights_out_01");
            Logger.LogInfo($"[Blackout] Bundle loaded: blackout sfx {(_powerDownClip != null ? _powerDownClip.length.ToString("0.0") + "s" : "MISSING")}, announcer {(_announcerClip != null ? _announcerClip.length.ToString("0.0") + "s" : "MISSING")}");
        }
    }
}
