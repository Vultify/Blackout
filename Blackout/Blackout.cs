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
using UnityEngine.Rendering.PostProcessing;

namespace Blackout
{
    [BepInPlugin("com.vultify.blackout", "Blackout", "1.0.0")]
    public class BlackoutPlugin : BaseUnityPlugin
    {
        private const string LabsLocationId = "laboratory";

        private const float RescanIntervalSec = 2f;
        // tuned by eye against live event footage
        private const float CutExposure = -2f;

        // the live Admin's key id, created server-side by BlackoutServer
        private const string BlackoutKeyTemplateId = "6a33c17933cff6b88c08902e";
        private static readonly string[] LockedDoorIds =
        {
            "door_Laboratory_Medical_corridor_floor_1_00006",
        };

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
                    _statusStarted = false;
                    _clockStarted = false;
                    _blackoutActive = false;
                    _killedLights.Clear();
                    _trackedLights.Clear();
                    _gearLights.Clear();
                    _originalLightmaps = null;
                    _subtitleText = null;
                    _ambienceSource = null;
                    DestroyPpVolume();
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

            if (_ppVolume == null)
            {
                CreatePpVolume();
            }

            _nextRescan = 0f;
            EnforceBlackout();
            PlayPowerDownSound();
            StartAmbience();
            Logger.LogInfo($"[Blackout] Power cut: {_killedLights.Count} lights killed, pp volume {(_ppVolume != null ? "on" : "OFF")}");
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

            if (_ambienceSource != null)
            {
                _ambienceSource.volume = _ambienceVolume.Value;
            }
        }

        private void ApplyGearBoost()
        {
            // rebase when the game changes intensity (toggles), then cancel half the exposure drop
            // (full cancellation overdrives specular reflections)
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

        private void CreatePpVolume()
        {
            var ppLayer = FindObjectOfType<PostProcessLayer>();
            if (ppLayer == null)
            {
                Logger.LogWarning("[Blackout] No PostProcessLayer found, exposure darkening unavailable");
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

        private void InspectAimedDoor()
        {
            var cam = Camera.main;
            if (cam == null)
            {
                return;
            }
            var hits = Physics.RaycastAll(cam.transform.position, cam.transform.forward, 8f);
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
