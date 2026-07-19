using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using UnityEngine;
using UnityEngine.Audio;

namespace Blackout
{
    [BepInPlugin("com.vultify.blackout", "Blackout", "1.0.0")]
    public class BlackoutPlugin : BaseUnityPlugin
    {
        private const string LabsLocationId = "laboratory";

        private const float RescanIntervalSec = 2f;

        // the live Admin's key id, created server-side by BlackoutServer
        private const string BlackoutKeyTemplateId = "6a33c17933cff6b88c08902e";
        private static readonly string[] LockedDoorIds =
        {
            "door_Laboratory_Medical_corridor_floor_1_00006",
        };

        // the live event's Exit_Switch trigger pose and its Boiler_Control_Panel_A wall
        // prop, both read from the live 1.0.6.5 scene files
        private const string AdminSwitchId = "blackout_admin_switch";
        private static readonly Vector3 AdminSwitchPos = new Vector3(-130.899f, 1.404f, -336.423f);
        private static readonly Vector3 AdminPanelPos = new Vector3(-130.749f, 1.052f, -336.428f);
        private static readonly Quaternion AdminPanelRot = new Quaternion(-0.5f, 0.5f, 0.5f, 0.5f);
        private const string AdminPanelDonorName = "Boiler_Control_Panel_A";

        private ConfigEntry<bool> _modEnabled;
        private ConfigEntry<bool> _labsOnly;
        private ConfigEntry<float> _delaySeconds;
        private ConfigEntry<float> _soundVolume;
        private ConfigEntry<float> _ambienceVolume;
        private ConfigEntry<bool> _announcementEnabled;
        private ConfigEntry<float> _announcementDelay;
        private ConfigEntry<bool> _subtitleEnabled;
        private ConfigEntry<string> _subtitleTextCfg;
        private ConfigEntry<KeyboardShortcut> _inspectDoorKey;

        private bool _inRaid;
        private bool _doorsLocked;
        private bool _exfilsDumped;
        private bool _lockdownApplied;
        private bool _exfilRowsHidden;
        private bool _adminSwitchSpawned;
        private bool _gatesActivated;
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
        private AudioClip _ambienceClip;
        private AudioSource _ambienceSource;
        private string _subtitleText;
        private float _subtitleUntil;
        private Texture2D _subtitleBg;
        private Texture2D _subtitleFrame;
        private GUIStyle _subtitleStyle;
        private bool _errorLogged;

        private readonly List<LightState> _killedLights = new List<LightState>();
        private readonly HashSet<Light> _trackedLights = new HashSet<Light>();
        private readonly List<EFT.Interactive.LampController> _switchedLamps = new List<EFT.Interactive.LampController>();
        private readonly HashSet<EFT.Interactive.LampController> _trackedLamps = new HashSet<EFT.Interactive.LampController>();
        private readonly List<GameObject> _disabledLightSceneRoots = new List<GameObject>();

        private LightmapData[] _originalLightmaps;
        private float _originalAmbientIntensity;
        private Color _originalAmbientLight;
        private float _originalReflectionIntensity;

        private struct LightState
        {
            public Light Light;
            public float Intensity;
        }

        // the p0 emissive shaders drive brightness through power/visibility floats and the
        // animated color pair - _EmissionColor alone is often already black
        private static readonly string[] EmissiveFloatProps = { "_EmissionPower", "_EmissionVisibility" };
        private static readonly string[] EmissiveColorProps = { "_EmissionColor", "_EmissiveColor", "_EmAnim1Color", "_EmAnim2Color" };

        private class EmissiveState
        {
            public readonly Dictionary<string, float> Floats = new Dictionary<string, float>();
            public readonly Dictionary<string, Color> Colors = new Dictionary<string, Color>();
            public bool StandardKeyword;
        }

        private readonly Dictionary<Material, EmissiveState> _dimmedEmissives = new Dictionary<Material, EmissiveState>();

        private void Awake()
        {
            _modEnabled = Config.Bind(
                "1. General",
                "Enable Mod",
                true,
                "Master toggle - enables or disables the entire mod");

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
                    "Volume of the blackout sound and announcement",
                    new AcceptableValueRange<float>(0f, 1f)));

            _ambienceVolume = Config.Bind(
                "3. Sound",
                "Ambience Volume",
                0.35f,
                new ConfigDescription(
                    "Volume of the event's dark ambience loop after the power cut. 0 disables it",
                    new AcceptableValueRange<float>(0f, 1f)));

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

            _inspectDoorKey = Config.Bind(
                "4. Debug",
                "Inspect Door Key",
                new KeyboardShortcut(KeyCode.F10),
                "Aim at a door and press to log its scene Id, key requirement and state - used to pick doors for the lock feature");

            LoadSoundBundle();
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
            var gameWorld = Singleton<GameWorld>.Instance;
            if (gameWorld == null || gameWorld.MainPlayer == null)
            {
                if (_inRaid)
                {
                    // raid ended; the scene (and everything we touched) is gone
                    _inRaid = false;
                    _doorsLocked = false;
                    _exfilsDumped = false;
                    _lockdownApplied = false;
                    _exfilRowsHidden = false;
                    _adminSwitchSpawned = false;
                    _gatesActivated = false;
                    _statusStarted = false;
                    _clockStarted = false;
                    _blackoutActive = false;
                    _killedLights.Clear();
                    _trackedLights.Clear();
                    _switchedLamps.Clear();
                    _trackedLamps.Clear();
                    _disabledLightSceneRoots.Clear();
                    // materials are assets that outlive the raid - un-dim them or the next
                    // raid (any map) inherits dead lamps
                    RestoreEmissiveMaterials();
                    _originalLightmaps = null;
                    _subtitleText = null;
                    _ambienceSource = null;
                }
                return;
            }

            _inRaid = true;

            if (!_doorsLocked && _modEnabled.Value
                && (!_labsOnly.Value || gameWorld.LocationId == LabsLocationId))
            {
                _doorsLocked = true;
                LockEventDoors();
            }

            // exfil points initialize later in the load than doors do - poll until they exist
            if (!_exfilsDumped && _modEnabled.Value
                && (!_labsOnly.Value || gameWorld.LocationId == LabsLocationId))
            {
                _exfilsDumped = DumpExfils();
            }

            if (!_lockdownApplied && _modEnabled.Value && gameWorld.LocationId == LabsLocationId)
            {
                _lockdownApplied = ApplyExtractLockdown();
            }

            if (_lockdownApplied && !_exfilRowsHidden)
            {
                _exfilRowsHidden = HideDisabledExfilRows();
            }

            if (_lockdownApplied && !_adminSwitchSpawned)
            {
                _adminSwitchSpawned = SpawnAdminSwitch();
            }

            if (_inspectDoorKey.Value.IsDown())
            {
                InspectAimedDoor();
            }

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

            _nextRescan = 0f;
            DisableLightScene();
            EnforceBlackout();
            PlayPowerDownSound();
            StartAmbience();
            Logger.LogInfo($"[Blackout] Power cut: {_killedLights.Count} lights killed, {_switchedLamps.Count} lamp fixtures switched off, {_dimmedEmissives.Count} emissive materials dimmed");
        }

        // the live event's dark preset simply doesn't load the map's *_LIGHT scene - it holds
        // the physical fixtures (glowing tubes, screens) and their lights; disabling its roots
        // is the runtime equivalent
        private void DisableLightScene()
        {
            for (var i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (!scene.isLoaded || scene.name.IndexOf("_LIGHT", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
                foreach (var root in scene.GetRootGameObjects())
                {
                    if (root != null && root.activeSelf)
                    {
                        root.SetActive(false);
                        _disabledLightSceneRoots.Add(root);
                    }
                }
                Logger.LogInfo($"[Blackout] Light scene '{scene.name}' disabled ({_disabledLightSceneRoots.Count} roots)");
            }
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

                    // player/bot gear lights (flashlights, lasers) stay untouched - real
                    // darkness needs no compensation, the game manages them
                    if (IsGearLight(light))
                    {
                        continue;
                    }
                    _killedLights.Add(new LightState { Light = light, Intensity = light.intensity });
                }

                // the game's own fixture switch: kills the lamp's emissive glass, flares and
                // lights together, exactly like shooting one out - the real physical blackout
                foreach (var lamp in FindObjectsOfType<EFT.Interactive.LampController>())
                {
                    if (lamp == null || _trackedLamps.Contains(lamp))
                    {
                        continue;
                    }
                    _trackedLamps.Add(lamp);
                    var lampState = lamp.LampState;
                    if (lampState == EFT.Interactive.Turnable.EState.Off
                        || lampState == EFT.Interactive.Turnable.EState.Destroyed)
                    {
                        continue;
                    }
                    try
                    {
                        lamp.Switch(EFT.Interactive.Turnable.EState.Off);
                        _switchedLamps.Add(lamp);
                    }
                    catch
                    {
                        // one broken lamp must not stop the sweep
                    }
                }

                DimEmissiveMaterials();

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
                }
            }

            foreach (var state in _killedLights)
            {
                if (state.Light != null && state.Light.enabled)
                {
                    state.Light.enabled = false;
                }
            }

            RenderSettings.ambientIntensity = 0f;
            RenderSettings.ambientLight = Color.black;
            RenderSettings.reflectionIntensity = 0f;

            if (_ambienceSource != null)
            {
                _ambienceSource.volume = _ambienceVolume.Value;
            }
        }

        // the glow that survives everything else: surfaces on EFT's emissive shader family
        // (sky ceiling wallpaper, lamp glass atlases, LED panels) plus Standard-shader
        // backlights - zero their shared materials' emission scene-wide
        private void DimEmissiveMaterials()
        {
            foreach (var mat in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (mat == null || mat.shader == null || _dimmedEmissives.ContainsKey(mat))
                {
                    continue;
                }
                var shaderName = mat.shader.name;
                var standard = shaderName == "Standard";
                if (!standard && !shaderName.Contains("Emissive"))
                {
                    continue;
                }
                var keywordOn = standard && mat.IsKeywordEnabled("_EMISSION");
                if (standard && !keywordOn)
                {
                    continue;
                }

                var state = new EmissiveState { StandardKeyword = keywordOn };
                foreach (var prop in EmissiveFloatProps)
                {
                    if (mat.HasProperty(prop))
                    {
                        state.Floats[prop] = mat.GetFloat(prop);
                        mat.SetFloat(prop, 0f);
                    }
                }
                foreach (var prop in EmissiveColorProps)
                {
                    if (mat.HasProperty(prop))
                    {
                        state.Colors[prop] = mat.GetColor(prop);
                        mat.SetColor(prop, Color.black);
                    }
                }
                if (state.Floats.Count == 0 && state.Colors.Count == 0 && !keywordOn)
                {
                    continue;
                }
                if (keywordOn)
                {
                    mat.DisableKeyword("_EMISSION");
                }
                _dimmedEmissives[mat] = state;
            }
        }

        private void RestoreEmissiveMaterials()
        {
            foreach (var pair in _dimmedEmissives)
            {
                var mat = pair.Key;
                if (mat == null)
                {
                    continue;
                }
                foreach (var prop in pair.Value.Floats)
                {
                    mat.SetFloat(prop.Key, prop.Value);
                }
                foreach (var prop in pair.Value.Colors)
                {
                    mat.SetColor(prop.Key, prop.Value);
                }
                if (pair.Value.StandardKeyword)
                {
                    mat.EnableKeyword("_EMISSION");
                }
            }
            _dimmedEmissives.Clear();
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

            foreach (var lamp in _switchedLamps)
            {
                if (lamp == null)
                {
                    continue;
                }
                try
                {
                    lamp.Switch(EFT.Interactive.Turnable.EState.On);
                }
                catch
                {
                    // keep restoring the rest
                }
            }
            _switchedLamps.Clear();
            _trackedLamps.Clear();

            foreach (var root in _disabledLightSceneRoots)
            {
                if (root != null)
                {
                    root.SetActive(true);
                }
            }
            _disabledLightSceneRoots.Clear();

            if (_originalLightmaps != null)
            {
                LightmapSettings.lightmaps = _originalLightmaps;
                _originalLightmaps = null;
            }
            RenderSettings.ambientIntensity = _originalAmbientIntensity;
            RenderSettings.ambientLight = _originalAmbientLight;
            RenderSettings.reflectionIntensity = _originalReflectionIntensity;
            RestoreEmissiveMaterials();
            StopAmbience();

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
            if (_powerDownClip == null || _soundVolume.Value <= 0f)
            {
                return;
            }

            var guiSounds = Singleton<EFT.UI.GUISounds>.Instance;
            if (guiSounds != null)
            {
                guiSounds.PlaySound(_powerDownClip, false, false, _soundVolume.Value);
                return;
            }

            var go = new GameObject("BlackoutSound");
            var source = go.AddComponent<AudioSource>();
            source.spatialBlend = 0f;
            source.volume = _soundVolume.Value;
            source.clip = _powerDownClip;
            source.Play();
            Destroy(go, _powerDownClip.length + 1f);
        }

        private void PlayAnnouncement()
        {
            if (_announcerClip == null || _soundVolume.Value <= 0f)
            {
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

            // no PA on this map: virtual speaker ring copied from the real speakers' profile
            var gameWorld = Singleton<GameWorld>.Instance;
            var player = gameWorld != null ? gameWorld.MainPlayer : null;
            if (player == null)
            {
                return;
            }

            var group = FindAmbientMixerGroup();
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
            Logger.LogInfo($"[Blackout] Speaker '{speaker.gameObject.name}' original pitch {originalPitch:0.000}, playing at 1.0");
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

        private void StartAmbience()
        {
            if (_ambienceClip == null || _ambienceVolume.Value <= 0f)
            {
                return;
            }

            var go = new GameObject("BlackoutAmbience");
            _ambienceSource = go.AddComponent<AudioSource>();
            _ambienceSource.clip = _ambienceClip;
            _ambienceSource.outputAudioMixerGroup = FindAmbientMixerGroup();
            _ambienceSource.loop = true;
            _ambienceSource.spatialBlend = 0f;
            _ambienceSource.volume = _ambienceVolume.Value;
            _ambienceSource.Play();
        }

        private void StopAmbience()
        {
            if (_ambienceSource != null)
            {
                Destroy(_ambienceSource.gameObject);
                _ambienceSource = null;
            }
        }

        private AudioMixerGroup FindAmbientMixerGroup()
        {
            // the effects ambient chain the real announcer speakers route through
            foreach (var g in Resources.FindObjectsOfTypeAll<AudioMixerGroup>())
            {
                if (g != null && g.name == "CommonAmbInEffects")
                {
                    return g;
                }
            }
            foreach (var s in FindObjectsOfType<AudioSource>())
            {
                if (s != null && s.enabled && s.outputAudioMixerGroup != null && s.isPlaying)
                {
                    return s.outputAudioMixerGroup;
                }
            }
            return null;
        }

        private void LockEventDoors()
        {
            var wanted = new HashSet<string>(LockedDoorIds);
            var locked = 0;
            foreach (var door in FindObjectsOfType<EFT.Interactive.Door>())
            {
                if (door == null || !wanted.Contains(door.Id))
                {
                    continue;
                }
                door.KeyId = BlackoutKeyTemplateId;
                door.DoorState = EFT.Interactive.EDoorState.Locked;
                // scenes can leave the door physically ajar, snap the hinge to its closed angle
                door.CurrentAngle = door.GetAngle(EFT.Interactive.EDoorState.Locked);
                locked++;
            }
            Logger.LogInfo($"[Blackout] Locked {locked}/{wanted.Count} event doors");
        }

        // recon for the extraction lockdown: names, statuses and switch wiring of every exfil
        private bool DumpExfils()
        {
            var controller = ExfiltrationControllerClass.Instance;
            if (controller == null || controller.ExfiltrationPoints == null
                || controller.ExfiltrationPoints.Length == 0)
            {
                return false;
            }
            foreach (var point in controller.ExfiltrationPoints)
            {
                if (point == null)
                {
                    continue;
                }
                try
                {
                    var name = point.Settings != null ? point.Settings.Name : "<no settings>";
                    var sw = point.Switch;
                    var swInfo = sw != null ? $"switch='{sw.Id}' prev='{(sw.PreviousSwitch != null ? sw.PreviousSwitch.Id : "none")}'" : "no switch";
                    Logger.LogInfo($"[Blackout EXFIL] '{name}' status={point.Status} {swInfo} pos={point.transform.position}");
                }
                catch (Exception ex)
                {
                    Logger.LogInfo($"[Blackout EXFIL] point dump failed: {ex.Message}");
                }
            }
            return true;
        }

        // the event's lockdown: only the two gates remain, dead consoles, admin switch to come
        private bool ApplyExtractLockdown()
        {
            var controller = ExfiltrationControllerClass.Instance;
            if (controller == null || controller.ExfiltrationPoints == null
                || controller.ExfiltrationPoints.Length == 0)
            {
                return false;
            }

            foreach (var point in controller.ExfiltrationPoints)
            {
                if (point == null || point.Settings == null)
                {
                    continue;
                }
                var name = point.Settings.Name;
                if (name == "lab_Parking_Gate" || name == "lab_Hangar_Gate")
                {
                    if (point.Status == EFT.Interactive.EExfiltrationStatus.NotPresent)
                    {
                        point.Status = EFT.Interactive.EExfiltrationStatus.UncompleteRequirements;
                    }
                    // consoles are dead during the blackout, activation comes from the admin office
                    if (point.Switch != null)
                    {
                        point.Switch.Operatable = false;
                    }
                }
                else
                {
                    point.Status = EFT.Interactive.EExfiltrationStatus.NotPresent;
                    // drop it from the extract list entirely and kill its whole switch chain
                    point.EligibleEntryPoints = Array.Empty<string>();
                    for (var sw = point.Switch; sw != null; sw = sw.PreviousSwitch)
                    {
                        sw.Operatable = false;
                    }
                }
            }
            Logger.LogInfo("[Blackout] Extract lockdown applied: gates only, consoles disabled");
            return true;
        }

        // the timers panel lists every point regardless of status; hide the disabled rows
        // the same way the game hides secret extracts
        private bool HideDisabledExfilRows()
        {
            var panel = FindObjectOfType<EFT.UI.ExtractionTimersPanel>();
            if (panel == null)
            {
                return false;
            }

            System.Collections.Generic.Dictionary<string, EFT.UI.BattleTimer.ExitTimerPanel> rows = null;
            foreach (var field in typeof(EFT.UI.ExtractionTimersPanel).GetFields(BindingFlags.NonPublic | BindingFlags.Instance))
            {
                rows = field.GetValue(panel) as System.Collections.Generic.Dictionary<string, EFT.UI.BattleTimer.ExitTimerPanel>;
                if (rows != null)
                {
                    break;
                }
            }
            if (rows == null || rows.Count == 0)
            {
                return false;
            }

            var hidden = 0;
            foreach (var pair in rows)
            {
                if (pair.Key == "lab_Parking_Gate" || pair.Key == "lab_Hangar_Gate")
                {
                    continue;
                }
                if (pair.Value != null)
                {
                    pair.Value.HideGameObject();
                    hidden++;
                }
            }
            Logger.LogInfo($"[Blackout] Hid {hidden} disabled extract rows");
            return true;
        }

        // the event's extraction switch: a clone of the parking gate console mounted in the
        // admin office, one flip activates both gates
        private bool SpawnAdminSwitch()
        {
            var controller = ExfiltrationControllerClass.Instance;
            if (controller == null || controller.ExfiltrationPoints == null
                || controller.ExfiltrationPoints.Length == 0)
            {
                return false;
            }

            EFT.Interactive.Switch donor = null;
            foreach (var point in controller.ExfiltrationPoints)
            {
                if (point != null && point.Settings != null
                    && point.Settings.Name == "lab_Parking_Gate" && point.Switch != null)
                {
                    donor = point.Switch;
                    break;
                }
            }
            if (donor == null)
            {
                Logger.LogWarning("[Blackout] Parking gate console not found, no admin switch");
                return true;
            }

            var go = Instantiate(donor.gameObject);
            go.name = AdminSwitchId;
            go.transform.position = AdminSwitchPos;
            go.transform.rotation = Quaternion.identity;

            var sw = go.GetComponent<EFT.Interactive.Switch>();
            sw.Id = AdminSwitchId;
            // stand-alone: no exfil, door, or switch chain of its own - we do the wiring
            sw.ExfiltrationPoint = null;
            sw.Door = null;
            sw.NextSwitches = Array.Empty<EFT.Interactive.Switch.SwitchAndOperation>();
            sw.PreviousSwitch = null;
            sw.AutoTurnOff = false;
            sw.DoorState = EFT.Interactive.EDoorState.Shut;
            sw.Operatable = true;
            sw.OnDoorStateChanged += OnAdminSwitchStateChanged;
            go.SetActive(true);

            // visible body: live mounts a Boiler_Control_Panel_A prop here - clone the real
            // one already loaded elsewhere in the map, at the live pose (real shader/material)
            var panelDonor = GameObject.Find(AdminPanelDonorName);
            if (panelDonor != null)
            {
                var panel = Instantiate(panelDonor, go.transform);
                panel.name = "blackout_admin_panel";
                panel.transform.position = AdminPanelPos;
                panel.transform.rotation = AdminPanelRot;
                panel.transform.localScale = new Vector3(0.7f, 0.7f, 0.7f);
                panel.SetActive(true);
                // the donor's LODGroup was cloned mid-cull and can keep the renderers hidden
                var lodGroup = panel.GetComponentInChildren<LODGroup>(true);
                if (lodGroup != null)
                {
                    Destroy(lodGroup);
                }
                foreach (var rend in panel.GetComponentsInChildren<Renderer>(true))
                {
                    if (!rend.gameObject.name.Contains("SHADOW"))
                    {
                        rend.enabled = true;
                        rend.forceRenderingOff = false;
                    }
                    Logger.LogInfo($"[Blackout] panel renderer '{rend.gameObject.name}' bounds center={rend.bounds.center} size={rend.bounds.size}");
                }
                // if the visible mesh ended up inside the wall (x beyond the wall face),
                // the live rotation read mirrored - swing it back out
                var lod0 = panel.GetComponentsInChildren<Renderer>(true);
                foreach (var rend in lod0)
                {
                    if (!rend.gameObject.name.Contains("SHADOW") && rend.bounds.center.x > -130.74f)
                    {
                        panel.transform.rotation = Quaternion.AngleAxis(180f, Vector3.up) * AdminPanelRot;
                        Logger.LogInfo("[Blackout] panel was inside the wall, flipped 180");
                        break;
                    }
                }
                Logger.LogInfo("[Blackout] Boiler control panel cloned onto the admin wall");
            }
            else
            {
                Logger.LogWarning("[Blackout] Boiler_Control_Panel_A not found in scene, switch stays invisible");
            }

            var col = go.GetComponentInChildren<Collider>(true);
            Logger.LogInfo($"[Blackout] Admin switch spawned at {go.transform.position} (donor '{donor.gameObject.name}', renderers={go.GetComponentsInChildren<Renderer>(true).Length}, collider {(col != null ? col.name : "MISSING")})");
            return true;
        }

        private void OnAdminSwitchStateChanged(EFT.Interactive.WorldInteractiveObject obj,
            EFT.Interactive.EDoorState prev, EFT.Interactive.EDoorState next)
        {
            if (next != EFT.Interactive.EDoorState.Open || _gatesActivated)
            {
                return;
            }
            _gatesActivated = true;
            ActivateGates();
        }

        private void ActivateGates()
        {
            var controller = ExfiltrationControllerClass.Instance;
            if (controller == null || controller.ExfiltrationPoints == null)
            {
                return;
            }
            foreach (var point in controller.ExfiltrationPoints)
            {
                if (point == null || point.Settings == null)
                {
                    continue;
                }
                var name = point.Settings.Name;
                if (name != "lab_Parking_Gate" && name != "lab_Hangar_Gate")
                {
                    continue;
                }
                try
                {
                    var sw = point.Switch;
                    if (sw != null && sw.DoorState == EFT.Interactive.EDoorState.Shut)
                    {
                        // the game's own remote-flip pattern (Switch.NextSwitches): runs the
                        // full vanilla path - console animation, gate door, alarm
                        sw.LockForInteraction();
                        sw.Interact(new EFT.Interactive.InteractionResult(EInteractionType.Open));
                    }
                    // the console's status write is gated on ConditionStatus, which our
                    // lockdown state may not satisfy - set it directly as well
                    var target = sw != null
                        ? sw.TargetStatus
                        : EFT.Interactive.EExfiltrationStatus.RegularMode;
                    point.ExternalSetStatus(target);
                    Logger.LogInfo($"[Blackout] Gate activated: {name} -> {target}");
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"[Blackout] Gate activation failed for {name}: {ex.Message}");
                }
            }
        }

        private void InspectAimedDoor()
        {
            var cam = Camera.main;
            if (cam == null)
            {
                return;
            }
            // long reach for ceilings; ignore trigger volumes (spawn/quest zones swallow the ray)
            var hits = Physics.RaycastAll(cam.transform.position, cam.transform.forward, 60f,
                Physics.AllLayers, QueryTriggerInteraction.Ignore);
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            EFT.Interactive.WorldInteractiveObject wio = null;
            foreach (var h in hits)
            {
                var candidate = h.collider.GetComponentInParent<EFT.Interactive.WorldInteractiveObject>();
                if (candidate != null)
                {
                    wio = candidate;
                    break;
                }
            }
            if (wio == null)
            {
                foreach (var h in hits)
                {
                    // skip bodies, we want static geometry for switch placement
                    if (h.collider.GetComponentInParent<Player>() != null)
                    {
                        continue;
                    }
                    Logger.LogInfo($"[Blackout DOOR] surface point={h.point} normal={h.normal} on '{h.collider.name}' (for switch placement)");
                    // material recon for tracking down still-glowing emissive surfaces
                    var rends = h.collider.GetComponentsInChildren<Renderer>(true);
                    if (rends.Length == 0 && h.collider.transform.parent != null)
                    {
                        rends = h.collider.transform.parent.GetComponentsInChildren<Renderer>(true);
                    }
                    foreach (var rend in rends)
                    {
                        foreach (var m in rend.sharedMaterials)
                        {
                            if (m != null)
                            {
                                Logger.LogInfo($"[Blackout DOOR] renderer '{rend.gameObject.name}' material '{m.name}' shader '{(m.shader != null ? m.shader.name : "null")}'");
                            }
                        }
                    }
                    return;
                }
                Logger.LogInfo("[Blackout DOOR] nothing static within 8m");
                return;
            }
            var path = wio.gameObject.name;
            var t = wio.transform.parent;
            for (var i = 0; i < 6 && t != null; i++, t = t.parent)
            {
                path = t.name + "/" + path;
            }
            Logger.LogInfo($"[Blackout DOOR] type={wio.GetType().Name} id='{wio.Id}' keyId='{wio.KeyId}' state={wio.DoorState} operatable={wio.Operatable} pos={wio.transform.position} :: {path}");
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
            _ambienceClip = bundle.LoadAsset<AudioClip>("amb_dark_lab");
            Logger.LogInfo($"[Blackout] Bundle loaded: blackout sfx {(_powerDownClip != null ? "ok" : "MISSING")}, announcer {(_announcerClip != null ? "ok" : "MISSING")}, ambience {(_ambienceClip != null ? "ok" : "MISSING")}");
        }
    }
}
