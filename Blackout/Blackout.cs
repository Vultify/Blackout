using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Audio.SpatialSystem;
using BepInEx;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

namespace Blackout
{
    [BepInPlugin("com.vultify.blackout", "Blackout", "1.0.0")]
    public class BlackoutPlugin : BaseUnityPlugin
    {
        private const string LabsLocationId = "laboratory";

        private const float RescanIntervalSec = 2f;
        // matched against live event footage: near-black with faint silhouettes
        private const float CutExposure = -2f;

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
        private bool _statusStarted;
        private Vector3 _startPos;
        private Vector2 _startLook;
        private float _accumMove;
        private float _accumLook;
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
        private float _nextAudioScan;
        private readonly HashSet<int> _scannedPlaying = new HashSet<int>();

        private readonly List<LightState> _killedLights = new List<LightState>();
        private readonly HashSet<Light> _trackedLights = new HashSet<Light>();
        private readonly Dictionary<Light, GearLightState> _gearLights = new Dictionary<Light, GearLightState>();

        private class GearLightState
        {
            public float Baseline;
            public float LastSet;
        }
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

        internal static BepInEx.Logging.ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
            new Harmony("com.vultify.blackout").PatchAll(Assembly.GetExecutingAssembly());
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

        // catches announcements played by ANY system: dumps every new voice-length source that starts playing
        private void ScanPlayingAudio()
        {
            if (Time.time < _nextAudioScan)
            {
                return;
            }
            _nextAudioScan = Time.time + 0.5f;

            var gw = Singleton<GameWorld>.Instance;
            var playerPos = gw != null && gw.MainPlayer != null ? gw.MainPlayer.Position : Vector3.zero;
            foreach (var s in FindObjectsOfType<AudioSource>())
            {
                if (s == null || !s.isPlaying || s.clip == null || s.clip.length < 2.5f || s.loop)
                {
                    continue;
                }
                var id = s.GetInstanceID();
                if (!_scannedPlaying.Add(id))
                {
                    continue;
                }
                var path = s.gameObject.name;
                var t = s.transform.parent;
                for (var i = 0; i < 6 && t != null; i++, t = t.parent)
                {
                    path = t.name + "/" + path;
                }
                var mixer = s.outputAudioMixerGroup;
                var dist = Vector3.Distance(s.transform.position, playerPos);
                if (s.rolloffMode == AudioRolloffMode.Custom)
                {
                    var curve = s.GetCustomCurve(AudioSourceCurveType.CustomRolloff);
                    if (curve != null)
                    {
                        var pts = "";
                        foreach (var k in curve.keys)
                        {
                            pts += $"({k.time:0.000},{k.value:0.000}) ";
                        }
                        Log.LogInfo($"[Blackout SCAN] curve '{s.gameObject.name}': {pts}");
                    }
                }
                Log.LogInfo($"[Blackout SCAN] playing clip='{s.clip.name}' len={s.clip.length:0.0}s vol={s.volume:0.00} blend={s.spatialBlend:0.00} spatialize={s.spatialize} min={s.minDistance:0.0} max={s.maxDistance:0.0} rolloffMode={s.rolloffMode} prio={s.priority} reverb={s.reverbZoneMix:0.00} spread={s.spread:0.0} bypassFx={s.bypassEffects}/{s.bypassListenerEffects}/{s.bypassReverbZones} mixer={(mixer != null ? mixer.audioMixer.name + "/" + mixer.name : "none")} dist={dist:0.0}m :: {path}");
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

        private void ApplyGearBoost()
        {
            // rebase when the game changes intensity (toggles), then cancel the exposure drop exactly
            var boost = Mathf.Pow(2f, -CutExposure) * 0.5f;
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
                var gear = _gearLights.ContainsKey(light) ? "gear" : "notGear";
                Logger.LogInfo($"[Blackout]   ENABLED {light.type} intensity={light.intensity:0.##} range={light.range:0.#} underPlayer={underPlayer} {gear} :: {path}");
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
                    _statusStarted = false;
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

            ScanPlayingAudio();

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

            // Started flips when the raid clock starts, still during the countdown -
            // real control is only proven by the player moving or looking around
            if (!_clockStarted)
            {
                var game = Singleton<AbstractGame>.Instance;
                if (game == null || game.Status != GameStatus.Started)
                {
                    return;
                }
                var player = gameWorld.MainPlayer;
                if (!_statusStarted)
                {
                    _statusStarted = true;
                    _startPos = player.Position;
                    _startLook = player.Rotation;
                    _accumMove = 0f;
                    _accumLook = 0f;
                    return;
                }
                var moveDelta = (player.Position - _startPos).magnitude;
                var lookDelta = (player.Rotation - _startLook).magnitude;
                _startPos = player.Position;
                _startLook = player.Rotation;
                if (moveDelta < 2f)
                {
                    // per-frame walking accumulates, spawn snaps and teleports do not
                    _accumMove += moveDelta;
                }
                _accumLook += lookDelta;
                if (_accumMove < 1.5f && _accumLook < 60f)
                {
                    return;
                }
                Logger.LogInfo($"[Blackout] Control detected (moved {_accumMove:0.0}m, looked {_accumLook:0} deg) at t={Time.time:0.0}");
                _clockStarted = true;
                _blackoutAt = Time.time + _delaySeconds.Value;
                return;
            }

            if (Time.time < _blackoutAt)
            {
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

            RenderSettings.ambientIntensity = 0f;
            RenderSettings.ambientLight = Color.black;
            RenderSettings.reflectionIntensity = 0f;

            // EFT's shaders light Labs through their own channels, so image-space exposure
            // is the only lever guaranteed to reach night-raid darkness
            if (_colorGrading != null)
            {
                _colorGrading.postExposure.value = CutExposure;
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
            _colorGrading.postExposure.Override(CutExposure);
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

            // best case: the map has real PA speakers, play through THEM - identical by definition
            // (their reverb/EQ companion sources can't be replicated on a raw AudioSource)
            var speakers = new List<AudioSource>();
            foreach (var s in FindObjectsOfType<AudioSource>())
            {
                if (s != null && s.gameObject.name.StartsWith("sound_disclamer"))
                {
                    speakers.Add(s);
                }
            }
            if (speakers.Count > 0)
            {
                Logger.LogInfo($"[Blackout] Announcement via {speakers.Count} map announcer speakers");
                foreach (var s in speakers)
                {
                    StartCoroutine(PlayThroughSpeaker(s));
                }
                return;
            }

            UnityEngine.Audio.AudioMixerGroup group = null;
            foreach (var g in Resources.FindObjectsOfTypeAll<UnityEngine.Audio.AudioMixerGroup>())
            {
                if (g != null && g.name == "CommonAmbInEffects")
                {
                    group = g;
                    break;
                }
            }
            if (group == null)
            {
                foreach (var s in FindObjectsOfType<AudioSource>())
                {
                    if (s != null && s.enabled && s.outputAudioMixerGroup != null)
                    {
                        group = s.outputAudioMixerGroup;
                        if (s.isPlaying)
                        {
                            break;
                        }
                    }
                }
            }

            var offsets = new[]
            {
                new Vector3(40f, 4f, 12f),
                new Vector3(-68f, 4f, -20f),
                new Vector3(25f, 4f, -75f),
                new Vector3(-30f, 4f, 95f)
            };
            foreach (var offset in offsets)
            {
                var go = new GameObject("BlackoutPaSpeaker");
                go.transform.position = player.Position + offset;
                var speaker = go.AddComponent<AudioSource>();
                speaker.clip = _announcerClip;
                speaker.outputAudioMixerGroup = group;
                speaker.spatialBlend = 1f;
                speaker.spread = 120f;
                speaker.priority = 64;
                speaker.minDistance = 15f;
                speaker.maxDistance = 80f;
                speaker.rolloffMode = AudioRolloffMode.Linear;
                speaker.volume = 0.58f * _soundVolume.Value;
                speaker.Play();
                Destroy(go, _announcerClip.length + 1f);
            }
        }

        private IEnumerator PlayThroughSpeaker(AudioSource speaker)
        {
            var originalClip = speaker.clip;
            var originalPitch = speaker.pitch;
            Log.LogInfo($"[Blackout] Speaker '{speaker.gameObject.name}' original pitch {originalPitch:0.000}, playing at 1.0");
            speaker.Stop();
            speaker.clip = _announcerClip;
            speaker.pitch = 1f;
            speaker.Play();
            yield return new WaitForSeconds(_announcerClip.length + 0.1f);
            if (speaker != null)
            {
                speaker.Stop();
                speaker.clip = originalClip;
                speaker.pitch = originalPitch;
            }
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

    [HarmonyPatch(typeof(BetterAudio), nameof(BetterAudio.PlayAtPoint),
        typeof(Vector3), typeof(AudioClip), typeof(BetterAudio.AudioSourceGroupType), typeof(int),
        typeof(float), typeof(EOcclusionTest), typeof(UnityEngine.Audio.AudioMixerGroup),
        typeof(bool), typeof(bool), typeof(bool), typeof(bool))]
    internal static class PlayAtPointSpy
    {
        private static void Postfix(BetterSource __result, Vector3 position, AudioClip clip,
            BetterAudio.AudioSourceGroupType sourceGroup, int rolloff, float volume, bool spatialize)
        {
            try
            {
                if (clip == null || clip.length < 1.5f)
                {
                    return; // door creaks and footsteps are noise, voice-length clips only
                }
                var log = BlackoutPlugin.Log;
                var gw = Singleton<GameWorld>.Instance;
                var dist = gw != null && gw.MainPlayer != null ? Vector3.Distance(position, gw.MainPlayer.Position) : -1f;
                log.LogInfo($"[Blackout SPY] PlayAtPoint clip='{clip.name}' len={clip.length:0.0}s ch={clip.channels} group={sourceGroup} rolloff={rolloff} vol={volume:0.00} spatialize={spatialize} distToPlayer={dist:0.0}m pos={position}");
                if (__result == null)
                {
                    log.LogInfo("[Blackout SPY]   result: null BetterSource");
                    return;
                }
                foreach (var s in __result.GetComponentsInChildren<AudioSource>(true))
                {
                    var mixer = s.outputAudioMixerGroup;
                    var curve = s.GetCustomCurve(AudioSourceCurveType.CustomRolloff);
                    var curveInfo = curve != null ? curve.length.ToString() : "none";
                    log.LogInfo($"[Blackout SPY]   src '{s.gameObject.name}': vol={s.volume:0.00} pitch={s.pitch:0.00} blend={s.spatialBlend:0.00} spatialize={s.spatialize} spatializePost={s.spatializePostEffects} min={s.minDistance:0.0} max={s.maxDistance:0.0} rolloffMode={s.rolloffMode} curveKeys={curveInfo} prio={s.priority} reverb={s.reverbZoneMix:0.00} doppler={s.dopplerLevel:0.00} spread={s.spread:0.0} bypassFx={s.bypassEffects}/{s.bypassListenerEffects}/{s.bypassReverbZones} mixer={(mixer != null ? mixer.audioMixer.name + "/" + mixer.name : "none")}");
                }
            }
            catch (Exception ex)
            {
                BlackoutPlugin.Log.LogWarning($"[Blackout SPY] {ex.Message}");
            }
        }
    }
}
