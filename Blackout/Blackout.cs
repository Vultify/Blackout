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
    // the client half of WTT-CommonLib and WTT-ContentBackport must be present, or the Wedge's gear and
    // the Admin key resolve to nothing client-side. hard-depend so a missing half errors clearly
    [BepInDependency("com.wtt.commonlib", BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency("com.wtt.contentbackport", BepInDependency.DependencyFlags.HardDependency)]
    public class BlackoutPlugin : BaseUnityPlugin
    {
        private const string LabsLocationId = "laboratory";

        private const float RescanIntervalSec = 5f;

        // the live Admin's key id, created server-side by BlackoutServer
        private const string BlackoutKeyTemplateId = "6a33c17933cff6b88c08902e";
        private static readonly string[] LockedDoorIds =
        {
            "door_Laboratory_Medical_corridor_floor_1_00006",
        };

        // the two vehicle-gate ramps, sized from their bot-zone spawn clusters plus margin. Live only
        // sends Black Division behind the gates AFTER the button press, so until the switch fires these
        // volumes are carved off the AI navmesh - nobody (least of all the key-carrying Wedge) can
        // wander behind a gate the player cannot open yet
        private static readonly Vector3[] RampCenters =
        {
            new Vector3(-170.8f, 2.7f, -224.9f), // parking gate
            new Vector3(-230.2f, 2.7f, -450.7f), // hangar gate
        };
        private static readonly Vector3[] RampSizes =
        {
            new Vector3(35f, 6f, 12f),
            new Vector3(38f, 6f, 12f),
        };
        private readonly List<GameObject> _rampBlockers = new List<GameObject>();
        private bool _rampsBlocked;

        // the live event's Exit_Switch trigger pose and its Boiler_Control_Panel_A wall
        // prop, both read from the live 1.0.6.5 scene files
        private const string AdminSwitchId = "blackout_admin_switch";
        private static readonly Vector3 AdminSwitchPos = new Vector3(-130.899f, 1.404f, -336.423f);
        private static readonly Vector3 AdminPanelPos = new Vector3(-130.749f, 1.052f, -336.428f);
        private static readonly Quaternion AdminPanelRot = new Quaternion(-0.5f, 0.5f, 0.5f, 0.5f);
        private const string AdminPanelDonorName = "Boiler_Control_Panel_A";

        // live 1.0.6.5 added a Marker_Board_B to the admin room's base scene (live level79) at
        // this exact pose - SPT lacks the instance but ships the prop, so it clones like the panel
        private const string WhiteboardDonorName = "Marker_Board_B";
        private static readonly Vector3 WhiteboardPos = new Vector3(-135.766f, 0.119f, -335.513f);
        private static readonly Quaternion WhiteboardRot = new Quaternion(-0.5f, -0.5f, -0.5f, 0.5f);

        // the only F12 option: a master on/off. everything else is fixed at the values tuned against
        // the live event, so the menu stays a single toggle
        private ConfigEntry<bool> _modEnabled;

        private const bool LabsOnly = true;   // Labs only, no other map
        private const float DelaySeconds = 15f;
        private const float AmbientLevel = 0f;   // full black, like the live event
        private const float EmergencyIntensity = 1.2f;
        private const float SoundVolume = 0.8f;
        private const bool AnnouncementEnabled = true;
        private const float AnnouncementDelay = 2f;
        private const bool SubtitleEnabled = true;
        private const string SubtitleText =
            "The facility has been switched to emergency power. Please remain where you are and await evacuation.";

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
        private bool _lampScanRetired;
        private AudioMixerGroup _ambientMixerGroup;
        private bool _ambientMixerResolved;
        private int _stableSweeps;
        private int _stableLightScans;
        private readonly HashSet<Light> _reportedRelights = new HashSet<Light>();
        private bool _isLabs;
        private readonly List<Light> _emergencyLights = new List<Light>();

        private bool _whiteboardSpawned;
        private GameObject _whiteboard;
        private string _raidCode;

        internal static BlackoutPlugin Instance;
        private EFT.Interactive.KeycardDoor _keypadDoor;
        private string _keypadEntry = "";
        private float _keypadResultUntil;
        private bool _keypadGranted;
        private bool _keypadPatched;

        private bool _ambientKilled;
        private Color _origAmbSky;
        private Color _origAmbEquator;
        private Color _origAmbGround;
        private float _origAmbIntensity;
        private Color _origNvSky;
        private Color _origNvEquator;
        private Color _origNvGround;
        private float _origNvIntensity;
        private AmbientLight[] _ambientLights;


        private void Awake()
        {
            Instance = this;
            _modEnabled = Config.Bind(
                "1. General",
                "Enable Mod",
                true,
                "Master toggle - enables or disables the entire mod");

            LoadSoundBundle();

            try
            {
                new KeycardActionsPatch().Enable();
                _keypadPatched = true;
            }
            catch (Exception ex)
            {
                Logger.LogError($"[Blackout] keypad patch failed, keycard doors stay vanilla: {ex.Message}");
            }
        }

        // during the blackout, aiming at a Labs card reader offers the emergency code keypad
        // instead of keycard swipes - the live event's PasscodeTerminal, rebuilt on 0.16.9
        private class KeycardActionsPatch : SPT.Reflection.Patching.ModulePatch
        {
            protected override MethodBase GetTargetMethod()
            {
                return typeof(GetActionsClass).GetMethod("smethod_13",
                    BindingFlags.Public | BindingFlags.Static);
            }

            [SPT.Reflection.Patching.PatchPostfix]
            private static void Postfix(ref ActionsReturnClass __result,
                GamePlayerOwner owner, EFT.Interactive.KeycardDoor door, bool isProxy)
            {
                var plugin = Instance;
                if (plugin == null || !plugin._blackoutActive || !plugin._isLabs
                    || plugin._raidCode == null || !isProxy
                    || door.DoorState != EFT.Interactive.EDoorState.Locked)
                {
                    return;
                }

                __result = new ActionsReturnClass
                {
                    Actions = new List<ActionsTypesClass>
                    {
                        new ActionsTypesClass
                        {
                            Name = "Enter emergency code",
                            Action = () => plugin.OpenKeypad(door),
                            Disabled = !door.Operatable,
                        },
                    },
                };
            }
        }

        private void OpenKeypad(EFT.Interactive.KeycardDoor door)
        {
            _keypadDoor = door;
            _keypadEntry = "";
            _keypadResultUntil = 0f;
            _keypadGranted = false;
            GamePlayerOwner.SetIgnoreInput(true);
            // the game re-hides the cursor every frame; onBeforeRender runs after all script
            // updates, so this write wins and the cursor stays solid instead of blinking
            Application.onBeforeRender += EnforceKeypadCursor;
        }

        private void CloseKeypad()
        {
            if (_keypadDoor == null)
            {
                return;
            }
            _keypadDoor = null;
            Application.onBeforeRender -= EnforceKeypadCursor;
            GamePlayerOwner.SetIgnoreInput(false);
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        private void EnforceKeypadCursor()
        {
            if (_keypadDoor == null)
            {
                return;
            }
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        private void SubmitKeypad()
        {
            if (_keypadDoor == null || _keypadEntry.Length != 4)
            {
                return;
            }
            if (_keypadEntry == _raidCode)
            {
                _keypadGranted = true;
                _keypadResultUntil = Time.unscaledTime + 0.8f;
                // no beep of our own - UnlockCoroutine's success path already plays it
                _keypadDoor.Unlock();
                Logger.LogInfo($"[Blackout] correct code entered, '{_keypadDoor.Id}' unlocked");
            }
            else
            {
                _keypadGranted = false;
                _keypadResultUntil = Time.unscaledTime + 0.8f;
                PlayDoorBeep(_keypadDoor.DeniedBeep);
                StartCoroutine(BlinkWrongFlicker(_keypadDoor));
                _keypadEntry = "";
            }
        }

        private void PlayDoorBeep(AudioClip clip)
        {
            if (clip == null || _keypadDoor == null)
            {
                return;
            }
            var audio = Singleton<BetterAudio>.Instance;
            if (audio != null)
            {
                audio.PlayAtPoint(_keypadDoor.transform.position, clip, 25f,
                    BetterAudio.AudioSourceGroupType.Environment, 100, 1f,
                    EOcclusionTest.None, null, false);
            }
        }

        // the live event's wrong-code feedback: the reader's own red LED flickers
        private IEnumerator BlinkWrongFlicker(EFT.Interactive.KeycardDoor door)
        {
            var lights = new List<Light>();
            if (door.Proxies != null)
            {
                foreach (var proxy in door.Proxies)
                {
                    if (proxy == null)
                    {
                        continue;
                    }
                    foreach (var light in proxy.GetComponentsInChildren<Light>(true))
                    {
                        if (light.name.ToLowerInvariant().Contains("wrong_flicker"))
                        {
                            lights.Add(light);
                        }
                    }
                }
            }
            for (var blink = 0; blink < 4; blink++)
            {
                foreach (var light in lights)
                {
                    light.enabled = true;
                    light.intensity = 2f;
                }
                yield return new WaitForSeconds(0.1f);
                foreach (var light in lights)
                {
                    light.enabled = false;
                }
                yield return new WaitForSeconds(0.08f);
            }
        }

        private void KeypadGui()
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            var e = Event.current;
            if (e.type == EventType.KeyDown)
            {
                if (e.keyCode == KeyCode.Escape)
                {
                    CloseKeypad();
                    e.Use();
                    return;
                }
                if (e.keyCode == KeyCode.Backspace && _keypadEntry.Length > 0)
                {
                    _keypadEntry = _keypadEntry.Substring(0, _keypadEntry.Length - 1);
                    e.Use();
                }
                else if ((e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter))
                {
                    SubmitKeypad();
                    e.Use();
                }
                else if (e.character >= '0' && e.character <= '9' && _keypadEntry.Length < 4
                    && Time.unscaledTime >= _keypadResultUntil)
                {
                    _keypadEntry += e.character;
                    e.Use();
                }
            }

            // the live PasscodeInputPanel (GameUIScene level46), drawn with its own ripped
            // sprites at its exact geometry: panel 224x229 bottom-center (center 150px above
            // the screen bottom), seven 28x50 slots at 28px pitch, 40x28 keys on a 48/32
            // grid. All sprites are authored at 2x these layout units. 1080p-reference scale
            var s = Screen.height / 1080f;
            const float panelW = 224f;
            const float panelH = 229f;
            var px = (Screen.width - panelW * s) / 2f;
            var py = Screen.height - 264.5f * s;
            var panelCx = px + panelW * s / 2f;
            var showResult = Time.unscaledTime < _keypadResultUntil;

            EnsureKeypadStyles();

            // shadow sticks out 15 units on every side (Shadow: stretch anchors, sizeDelta 30)
            DrawKeypadTex("Background_Shadow", new Rect(px - 15f * s, py - 15f * s, (panelW + 30f) * s, (panelH + 30f) * s), 1f);
            DrawKeypadTex("Background", new Rect(px, py, panelW * s, panelH * s), 1f);

            // display substrate 214x52 centered 31 below panel top; result swaps the art
            var subRect = new Rect(panelCx - 107f * s, py + 5f * s, 214f * s, 52f * s);
            DrawKeypadTex(showResult ? (_keypadGranted ? "Substrate_Green" : "Substrate_Red") : "Substrate_Gray", subRect, 1f);
            if (showResult)
            {
                // the big Light flash under the strip, fading out over the result window
                var fade = Mathf.Clamp01((_keypadResultUntil - Time.unscaledTime) / 0.8f);
                var lightRect = new Rect(panelCx - 150f * s, subRect.y + subRect.height / 2f + (84f - 74f) * s, 300f * s, 148f * s);
                DrawKeypadTex(_keypadGranted ? "Light_Green" : "Light_Red", lightRect, fade);
                DrawKeypadTex(_keypadGranted ? "Flashing_Green" : "Flashing_Red", subRect, fade);
            }

            // seven digit slots at 28px pitch (holder 220 wide, first slot center x=26)
            var holderLeft = panelCx - 110f * s;
            var holderTop = py + 2.5f * s;
            _keypadSlotStyle.fontSize = Mathf.RoundToInt(30f * s);
            for (var slot = 0; slot < 7; slot++)
            {
                var cx = holderLeft + (26f + 28f * slot) * s;
                var cy = holderTop + 28f * s;
                DrawKeypadTex("Number_Enter", new Rect(cx - 14f * s, cy - 14.5f * s, 28f * s, 39f * s), 1f);

                var typed = slot < _keypadEntry.Length;
                _keypadSlotStyle.normal.textColor = showResult
                    ? (_keypadGranted ? new Color(0.65f, 1f, 0.65f) : new Color(1f, 0.5f, 0.5f))
                    : (typed ? new Color(0.9f, 0.94f, 1f) : new Color(0.3f, 0.33f, 0.37f));
                GUI.Label(new Rect(cx - 14f * s, cy - 25f * s, 28f * s, 50f * s),
                    typed ? _keypadEntry[slot].ToString() : "0", _keypadSlotStyle);
            }

            if (_keypadGranted && !showResult)
            {
                CloseKeypad();
                return;
            }

            // 3x4 key grid: holder center 26 below panel center; keys 40x28, visuals 42x30
            var keys = new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "X", "0", "E" };
            var gridLeft = panelCx - 70f * s;
            var gridTop = py + (114.5f + 26f - 65f) * s;
            _keypadButtonStyle.fontSize = Mathf.RoundToInt(17f * s);
            var mouse = Event.current.mousePosition;
            for (var i = 0; i < keys.Length; i++)
            {
                var col = i % 3;
                var row = i / 3;
                var bcx = gridLeft + (22f + 48f * col) * s;
                var bcy = gridTop + (17f + 32f * row) * s;
                var hitRect = new Rect(bcx - 20f * s, bcy - 14f * s, 40f * s, 28f * s);
                var visRect = new Rect(bcx - 21f * s, bcy - 15f * s, 42f * s, 30f * s);

                DrawKeypadTex(hitRect.Contains(mouse) ? "Button_Highlighted" : "Button_Normal", visRect, 1f);
                if (keys[i] == "X")
                {
                    DrawKeypadTex("Icon_Del_Static", new Rect(bcx - 7.5f * s, bcy - 8.5f * s, 15f * s, 17f * s), 1f);
                }
                else if (keys[i] == "E")
                {
                    DrawKeypadTex("Icon_Enter_Static", new Rect(bcx - 9.5f * s, bcy - 9.5f * s, 19f * s, 19f * s), 1f);
                }
                else
                {
                    GUI.Label(hitRect, keys[i], _keypadButtonStyle);
                }

                if (!GUI.Button(hitRect, GUIContent.none, GUIStyle.none))
                {
                    continue;
                }
                if (keys[i] == "X")
                {
                    if (_keypadEntry.Length > 0)
                    {
                        _keypadEntry = "";
                    }
                    else
                    {
                        CloseKeypad();
                    }
                }
                else if (keys[i] == "E")
                {
                    SubmitKeypad();
                }
                else if (_keypadEntry.Length < 4 && Time.unscaledTime >= _keypadResultUntil)
                {
                    _keypadEntry += keys[i];
                }
            }
        }

        private GUIStyle _keypadSlotStyle;
        private GUIStyle _keypadButtonStyle;
        private Dictionary<string, Texture2D> _keypadTex;

        private void DrawKeypadTex(string name, Rect rect, float alpha)
        {
            if (!_keypadTex.TryGetValue(name, out var tex) || tex == null)
            {
                return;
            }
            GUI.color = new Color(1f, 1f, 1f, alpha);
            GUI.DrawTexture(rect, tex, ScaleMode.StretchToFill, true);
            GUI.color = Color.white;
        }

        private void EnsureKeypadStyles()
        {
            if (_keypadSlotStyle != null)
            {
                return;
            }
            _keypadTex = new Dictionary<string, Texture2D>();
            foreach (var name in new[]
            {
                "Background", "Background_Shadow", "Substrate_Gray", "Substrate_Red",
                "Substrate_Green", "Flashing_Red", "Flashing_Green", "Light_Red",
                "Light_Green", "Button_Normal", "Button_Highlighted", "Button_Disabled",
                "Icon_Del_Static", "Icon_Enter_Static", "Number_Enter",
            })
            {
                _keypadTex[name] = LoadEmbeddedTexture($"Blackout.keypad.{name}.png");
            }
            _keypadSlotStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 30,
                alignment = TextAnchor.MiddleCenter,
            };
            _keypadButtonStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 17,
                alignment = TextAnchor.MiddleCenter,
            };
            _keypadButtonStyle.normal.textColor = new Color(0.87f, 0.9f, 0.93f);

            // live renders in bender; the game ships it as a legacy Font too (same lookup
            // the announcement subtitle uses, confirmed in-game)
            foreach (var font in Resources.FindObjectsOfTypeAll<Font>())
            {
                if (font != null && font.name.ToLowerInvariant().Contains("bender"))
                {
                    _keypadSlotStyle.font = font;
                    _keypadButtonStyle.font = font;
                    break;
                }
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
                    CloseKeypad();
                    _inRaid = false;
                    _doorsLocked = false;
                    _exfilsDumped = false;
                    _lockdownApplied = false;
                    _exfilRowsHidden = false;
                    _adminSwitchSpawned = false;
                    _whiteboardSpawned = false;
                    _whiteboard = null;
                    _raidCode = null;
                    if (_boardRt != null)
                    {
                        _boardRt.Release();
                        _boardRt = null;
                    }
                    _gatesActivated = false;
                    UnblockGateRamps();
                    _statusStarted = false;
                    _clockStarted = false;
                    _blackoutActive = false;
                    _killedLights.Clear();
                    _trackedLights.Clear();
                    _switchedLamps.Clear();
                    _trackedLamps.Clear();
                    _disabledLightSceneRoots.Clear();
                    _stableSweeps = 0;
                    _stableLightScans = 0;
                    _reportedRelights.Clear();
                    // materials are assets that outlive the raid - un-dim them or the next
                    // raid (any map) inherits dead lamps
                    RestoreEmissiveMaterials();
                    RestoreAmbient();
                    DestroyEmergencyLights();
                    _originalLightmaps = null;
                    _subtitleText = null;
                }
                return;
            }

            _inRaid = true;

            if (!_doorsLocked && _modEnabled.Value
                && (!LabsOnly || gameWorld.LocationId == LabsLocationId))
            {
                _doorsLocked = true;
                LockEventDoors();
            }

            // exfil points initialize later in the load than doors do - poll until they exist
            if (!_exfilsDumped && _modEnabled.Value
                && (!LabsOnly || gameWorld.LocationId == LabsLocationId))
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

            if (_adminSwitchSpawned && !_whiteboardSpawned)
            {
                _whiteboardSpawned = SpawnWhiteboard();
            }

            if (_lockdownApplied && !_rampsBlocked && !_gatesActivated)
            {
                _rampsBlocked = BlockGateRamps();
            }

            // walked away, died, or the blackout ended - drop the keypad and give input back
            if (_keypadDoor != null)
            {
                var player = gameWorld.MainPlayer;
                if (!_blackoutActive || player == null || player.HealthController?.IsAlive != true
                    || (player.Position - _keypadDoor.transform.position).sqrMagnitude > 16f)
                {
                    CloseKeypad();
                }
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
                if (!_announcementPlayed && AnnouncementEnabled
                    && _announcerClip != null && Time.time >= _announcementAt)
                {
                    _announcementPlayed = true;
                    PlayAnnouncement();
                    if (SubtitleEnabled && !string.IsNullOrWhiteSpace(SubtitleText))
                    {
                        _subtitleText = SubtitleText;
                        _subtitleUntil = Time.time + _announcerClip.length + 0.5f;
                    }
                }
                return;
            }

            if (LabsOnly && gameWorld.LocationId != LabsLocationId)
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
                _blackoutAt = Time.time + DelaySeconds;
                // resolve the mixer group now rather than on the cut frame, it scans every
                // loaded AudioMixerGroup and there are seconds to spare before the lights go
                FindAmbientMixerGroup();
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
            _announcementAt = Time.time + AnnouncementDelay;

            _originalAmbientIntensity = RenderSettings.ambientIntensity;
            _originalAmbientLight = RenderSettings.ambientLight;
            _originalReflectionIntensity = RenderSettings.reflectionIntensity;

            // some maps carry baked lightmaps (Labs doesn't, confirmed 0 there)
            _originalLightmaps = LightmapSettings.lightmaps;
            LightmapSettings.lightmaps = new LightmapData[0];

            _nextRescan = 0f;
            _stableSweeps = 0;
            _stableLightScans = 0;
            _isLabs = Singleton<GameWorld>.Instance?.LocationId == LabsLocationId;

            DisableLightScene();
            SpawnEmergencyLights();
            EnforceBlackout();
            PlayPowerDownSound();
            Logger.LogInfo($"[Blackout] Power cut: {_killedLights.Count} lights killed, {_switchedLamps.Count} lamp fixtures switched off, {_dimmedEmissives.Count} emissive materials dimmed");
        }

        private struct EmergencyLight
        {
            public Vector3 Pos;
            public Quaternion Rot;
            public Color Col;
            public float Range;
            public float SpotAngle;
            public bool Spot;

            public EmergencyLight(Vector3 pos, Quaternion rot, Color col, float range, float spotAngle, bool spot)
            {
                Pos = pos; Rot = rot; Col = col; Range = range; SpotAngle = spotAngle; Spot = spot;
            }
        }

        // the live dark scene's 15 amber emergency floods (5 fixtures x 3 lights), poses/colors/
        // ranges read from archived level711. Baked intensity is 0 - live animates it at runtime,
        // so ours rides the config slider instead
        private static readonly EmergencyLight[] EmergencyLights =
        {
            new EmergencyLight(new Vector3(-226.1066f, 4.6192f, -426.5847f), new Quaternion(-0.765146f, 0.037084f, -0.031117f, 0.642034f), new Color(0.7426f, 0.4763f, 0f), 10f, 45f, true),
            new EmergencyLight(new Vector3(-175.777f, 5.318f, -249.235f), new Quaternion(0.642034f, 0.031117f, 0.037084f, 0.765146f), new Color(0.7426f, 0.4763f, 0f), 10f, 45f, true),
            new EmergencyLight(new Vector3(-129.822f, -4.3899f, -244.9065f), new Quaternion(-0.031117f, 0.642034f, 0.765146f, -0.037084f), new Color(0.7426f, 0.4763f, 0f), 10f, 45f, true),
            new EmergencyLight(new Vector3(-165.6067f, 5.3204f, -249.235f), new Quaternion(0.536959f, 0.353343f, 0.421098f, 0.639922f), new Color(0.7426f, 0.4763f, 0f), 10f, 45f, true),
            new EmergencyLight(new Vector3(-237.0126f, 4.6047f, -426.5867f), new Quaternion(-0.642199f, 0.417618f, -0.350423f, 0.538869f), new Color(0.7426f, 0.4763f, 0f), 10f, 45f, true),
            new EmergencyLight(new Vector3(-165.6067f, 5.3204f, -249.235f), new Quaternion(-0.536959f, -0.353343f, 0.421098f, 0.639922f), new Color(0.7412f, 0.4745f, 0f), 5f, 30f, false),
            new EmergencyLight(new Vector3(-237.0126f, 4.6047f, -426.5867f), new Quaternion(-0.642198f, 0.417618f, 0.350424f, -0.538869f), new Color(0.7412f, 0.4745f, 0f), 5f, 30f, false),
            new EmergencyLight(new Vector3(-226.1066f, 4.6192f, -426.5847f), new Quaternion(-0.765146f, 0.037084f, 0.031117f, -0.642034f), new Color(0.7412f, 0.4745f, 0f), 5f, 30f, false),
            new EmergencyLight(new Vector3(-175.777f, 5.318f, -249.235f), new Quaternion(-0.642034f, -0.031117f, 0.037084f, 0.765146f), new Color(0.7412f, 0.4745f, 0f), 5f, 30f, false),
            new EmergencyLight(new Vector3(-129.822f, -4.3899f, -244.9065f), new Quaternion(0.031117f, -0.642034f, 0.765146f, -0.037084f), new Color(0.7412f, 0.4745f, 0f), 5f, 30f, false),
            new EmergencyLight(new Vector3(-129.822f, -4.3899f, -244.9065f), new Quaternion(0.031117f, -0.642034f, 0.765146f, -0.037084f), new Color(0.7426f, 0.4763f, 0f), 10f, 45f, true),
            new EmergencyLight(new Vector3(-175.777f, 5.318f, -249.235f), new Quaternion(-0.642034f, -0.031117f, 0.037084f, 0.765146f), new Color(0.7426f, 0.4763f, 0f), 10f, 45f, true),
            new EmergencyLight(new Vector3(-226.1066f, 4.6192f, -426.5847f), new Quaternion(-0.765146f, 0.037084f, 0.031117f, -0.642034f), new Color(0.7426f, 0.4763f, 0f), 10f, 45f, true),
            new EmergencyLight(new Vector3(-237.0126f, 4.6047f, -426.5867f), new Quaternion(-0.642198f, 0.417618f, 0.350424f, -0.538869f), new Color(0.7426f, 0.4763f, 0f), 10f, 45f, true),
            new EmergencyLight(new Vector3(-165.6067f, 5.3204f, -249.235f), new Quaternion(-0.536959f, -0.353343f, 0.421098f, 0.639922f), new Color(0.7426f, 0.4763f, 0f), 10f, 45f, true),
        };

        private void SpawnEmergencyLights()
        {
            // the poses are Labs world coordinates - meaningless anywhere else
            if (!_isLabs)
            {
                return;
            }
            foreach (var spec in EmergencyLights)
            {
                var go = new GameObject("blackout_emergency");
                go.transform.SetPositionAndRotation(spec.Pos, spec.Rot);
                var light = go.AddComponent<Light>();
                light.type = spec.Spot ? LightType.Spot : LightType.Point;
                light.color = spec.Col;
                light.range = spec.Range;
                light.spotAngle = spec.SpotAngle;
                light.shadows = LightShadows.None;
                light.intensity = EmergencyIntensity;
                _emergencyLights.Add(light);
            }
            Logger.LogInfo($"[Blackout] {_emergencyLights.Count} emergency lights spawned (live level711 poses)");
        }

        private void DestroyEmergencyLights()
        {
            foreach (var light in _emergencyLights)
            {
                if (light != null)
                {
                    Destroy(light.gameObject);
                }
            }
            _emergencyLights.Clear();
        }

        // EFT ambient is its own pipeline, not RenderSettings: LevelSettings re-applies its
        // serialized ambient colors on Camera.onPreCull every frame (zeroing RenderSettings
        // loses that race), and AmbientLight pushes probe SH + _EFT_Ambient shader globals.
        // Rewriting the fields LevelSettings enforces turns the game's own per-frame
        // re-apply into the blackout's enforcer; the SH push is re-zeroed per frame
        private void KillAmbient()
        {
            var settings = Singleton<LevelSettings>.Instance;
            if (settings != null)
            {
                if (!_ambientKilled)
                {
                    _origAmbSky = settings.SkyColor;
                    _origAmbEquator = settings.EquatorColor;
                    _origAmbGround = settings.GroundColor;
                    _origAmbIntensity = settings.AmbientIntensity;
                    _origNvSky = settings.NightVisionSkyColor;
                    _origNvEquator = settings.NightVisionEquatorColor;
                    _origNvGround = settings.NightVisionGroundColor;
                    _origNvIntensity = settings.NightVisionAmbientIntensity;
                    _ambientKilled = true;
                    Logger.LogInfo($"[Blackout] LevelSettings ambient taken over (level {AmbientLevel:0.00})");
                }

                // scaled from the saved originals every frame - the F12 slider applies live
                var keep = AmbientLevel;
                settings.SkyColor = _origAmbSky * keep;
                settings.EquatorColor = _origAmbEquator * keep;
                settings.GroundColor = _origAmbGround * keep;
                settings.AmbientIntensity = _origAmbIntensity * keep;
                settings.NightVisionSkyColor = _origNvSky * keep;
                settings.NightVisionEquatorColor = _origNvEquator * keep;
                settings.NightVisionGroundColor = _origNvGround * keep;
                settings.NightVisionAmbientIntensity = _origNvIntensity * keep;
            }

            if (_ambientLights == null)
            {
                _ambientLights = FindObjectsOfType<AmbientLight>();
                Logger.LogInfo($"[Blackout] AmbientLight components found: {_ambientLights.Length}");
            }
            foreach (var ambient in _ambientLights)
            {
                if (ambient == null)
                {
                    continue;
                }
                ambient.SetSH(default(UnityEngine.Rendering.SphericalHarmonicsL2));
                ambient.SetReflectionIntensity(0f);
            }
        }

        private void RestoreAmbient()
        {
            _ambientLights = null;
            if (!_ambientKilled)
            {
                return;
            }
            _ambientKilled = false;
            var settings = Singleton<LevelSettings>.Instance;
            if (settings == null)
            {
                return;
            }
            settings.SkyColor = _origAmbSky;
            settings.EquatorColor = _origAmbEquator;
            settings.GroundColor = _origAmbGround;
            settings.AmbientIntensity = _origAmbIntensity;
            settings.NightVisionSkyColor = _origNvSky;
            settings.NightVisionEquatorColor = _origNvEquator;
            settings.NightVisionGroundColor = _origNvGround;
            settings.NightVisionAmbientIntensity = _origNvIntensity;
            settings.ApplySettings();
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
            KillAmbient();

            // the F12 slider applies live; 0 switches them off without despawning
            foreach (var emergency in _emergencyLights)
            {
                if (emergency != null && emergency.intensity != EmergencyIntensity)
                {
                    emergency.intensity = EmergencyIntensity;
                }
            }

            // scene scans are pricey, discover new lights on an interval,
            // but re-assert state on already-tracked ones every frame (ToD re-enables the sun)
            if (Time.time >= _nextRescan)
            {
                _nextRescan = Time.time + RescanIntervalSec;
                if (_stableLightScans < 6)
                {
                    var lightsBefore = _killedLights.Count;
                    foreach (var light in FindObjectsOfType<Light>())
                    {
                        if (light == null || _trackedLights.Contains(light))
                        {
                            continue;
                        }
                        // leave alone entirely: muzzle flashes and pooled effect lights
                        // (gunfire/impacts/grenades) belong in the dark, and the red keycard
                        // LEDs are the one thing the live dark scene keeps lit
                        var lightName = light.name.ToLowerInvariant();
                        if (lightName.Contains("muzzle") || lightName.Contains("lightofpool")
                            || lightName.Contains("red_light") || lightName.Contains("blackout_emergency"))
                        {
                            continue;
                        }

                        // Labs door-status LEDs (flicker rigs, keycard state lamps) - the live
                        // dark scene ships all of these enabled and script-driven, so the game
                        // keeps animating them like the event does. Labs-gated: elsewhere these
                        // name fragments could match real lamps
                        if (_isLabs && (lightName.Contains("wrong_flicker") || lightName.Contains("cantescape")
                            || lightName.Contains("everything") || lightName.Contains("interacting")
                            || lightName.Contains("ready") || lightName.Contains("wait")))
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
                        // evidence line: does anything real ever show up after the first pass?
                        if (_stableLightScans > 0 || _killedLights.Count > 0 && Time.time > _blackoutAt + RescanIntervalSec)
                        {
                            Logger.LogInfo($"[Blackout] LATE light discovered: '{light.name}' type={light.type} at t+{Time.time - _blackoutAt:0}s");
                        }
                        _killedLights.Add(new LightState { Light = light, Intensity = light.intensity });
                    }
                    _stableLightScans = _killedLights.Count == lightsBefore ? _stableLightScans + 1 : 0;
                    if (_stableLightScans == 6)
                    {
                        Logger.LogInfo($"[Blackout] Light discovery retired: {_killedLights.Count} scene lights held dark");
                    }
                }

                // the lamp and material sweeps are whole-scene scans - once they stop finding
                // anything new for a few passes, retire them (the light scan stays, lights
                // keep appearing over a raid)
                if (_stableSweeps < 3)
                {
                    var lampsBefore = _trackedLamps.Count;
                    var emissivesBefore = _dimmedEmissives.Count;
                    var scannedLamps = false;

                    // the game's own fixture switch: kills the lamp's emissive glass, flares
                    // and lights together, exactly like shooting one out
                    // FindObjectsOfType walks the whole scene whether or not anything matches,
                    // and Labs has no LampControllers at all - 44ms a pass for nothing. Once a
                    // scan comes back with none, stop scanning: fixtures never appear mid-raid.
                    foreach (var lamp in _lampScanRetired
                        ? System.Array.Empty<EFT.Interactive.LampController>()
                        : FindObjectsOfType<EFT.Interactive.LampController>())
                    {
                        scannedLamps = true;
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

                    if (!_lampScanRetired && !scannedLamps && _trackedLamps.Count == 0)
                    {
                        _lampScanRetired = true;
                        Logger.LogInfo("[Blackout] No lamp fixtures on this map, lamp sweep retired");
                    }
                    DimEmissiveMaterials();

                    _stableSweeps = _trackedLamps.Count == lampsBefore && _dimmedEmissives.Count == emissivesBefore
                        ? _stableSweeps + 1
                        : 0;
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
                }
            }

            foreach (var state in _killedLights)
            {
                if (state.Light != null && state.Light.enabled)
                {
                    // evidence line: does the game actually re-enable killed lights (the
                    // "sun comes back" claim)? throttled to one report per offender
                    if (_reportedRelights.Add(state.Light))
                    {
                        Logger.LogInfo($"[Blackout] RE-ENABLED by game: '{state.Light.name}' type={state.Light.type} at t+{Time.time - _blackoutAt:0}s");
                    }
                    state.Light.enabled = false;
                }
            }

            RenderSettings.ambientIntensity = 0f;
            RenderSettings.ambientLight = Color.black;
            RenderSettings.reflectionIntensity = 0f;
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
            RestoreAmbient();
            DestroyEmergencyLights();

            Logger.LogInfo("[Blackout] Power restored (mod disabled mid-raid)");
        }

        private void OnGUI()
        {
            if (_keypadDoor != null)
            {
                KeypadGui();
            }

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
            if (_powerDownClip == null || SoundVolume <= 0f)
            {
                return;
            }

            var guiSounds = Singleton<EFT.UI.GUISounds>.Instance;
            if (guiSounds != null)
            {
                guiSounds.PlaySound(_powerDownClip, false, false, SoundVolume);
                return;
            }

            var go = new GameObject("BlackoutSound");
            var source = go.AddComponent<AudioSource>();
            source.spatialBlend = 0f;
            source.volume = SoundVolume;
            source.clip = _powerDownClip;
            source.Play();
            Destroy(go, _powerDownClip.length + 1f);
        }

        private void PlayAnnouncement()
        {
            if (_announcerClip == null || SoundVolume <= 0f)
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
                speaker.volume = 0.58f * SoundVolume;
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

        // scanning every loaded AudioMixerGroup costs ~40ms, so resolve it once and keep it
        private AudioMixerGroup FindAmbientMixerGroup()
        {
            if (_ambientMixerResolved)
            {
                return _ambientMixerGroup;
            }
            _ambientMixerGroup = FindAmbientMixerGroupUncached();
            _ambientMixerResolved = true;
            return _ambientMixerGroup;
        }

        private AudioMixerGroup FindAmbientMixerGroupUncached()
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
                // the arsenal only opens with its key, kicking it in stays off the menu
                door.CanBeBreached = false;
                door.CanInteractWithBreach = false;
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

        // the raid's emergency code lives on the admin room whiteboard, at the live event's own
        // board pose - rolled fresh every raid; step 2 wires the keypad doors to check against it
        private bool SpawnWhiteboard()
        {
            _raidCode = UnityEngine.Random.Range(0, 10000).ToString("D4");

            var donor = GameObject.Find(WhiteboardDonorName);
            if (donor == null)
            {
                Logger.LogWarning("[Blackout] Marker_Board_B not found in scene, code stays log-only");
                Logger.LogInfo($"[Blackout] raid code {_raidCode}");
                return true;
            }

            _whiteboard = Instantiate(donor);
            _whiteboard.name = "blackout_whiteboard";
            _whiteboard.transform.position = WhiteboardPos;
            _whiteboard.transform.rotation = WhiteboardRot;
            _whiteboard.transform.localScale = Vector3.one;
            var lodGroup = _whiteboard.GetComponentInChildren<LODGroup>(true);
            if (lodGroup != null)
            {
                Destroy(lodGroup);
            }
            Bounds bounds = default;
            var hasBounds = false;
            Renderer lod0 = null;
            foreach (var rend in _whiteboard.GetComponentsInChildren<Renderer>(true))
            {
                var name = rend.gameObject.name;
                // LOD0 only - with the LODGroup gone, an enabled LOD1 z-fights over the
                // detailed mesh and flattens the board's corner mounts and frame
                if (name.Contains("SHADOW") || name.Contains("LOD1"))
                {
                    rend.enabled = false;
                    continue;
                }
                rend.enabled = true;
                rend.forceRenderingOff = false;
                if (name.Contains("LOD0"))
                {
                    lod0 = rend;
                }
                if (!hasBounds)
                {
                    bounds = rend.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(rend.bounds);
                }
                Logger.LogInfo($"[Blackout] whiteboard renderer '{name}' bounds center={rend.bounds.center} size={rend.bounds.size}");
            }
            _whiteboard.SetActive(true);

            // wall plane is YZ at x~-135.8, board face toward the room (+X); mirrored-read trap
            // like the panel: a mesh sunk past the wall means the rotation needs the 180 swing
            if (hasBounds && bounds.center.x < -135.9f)
            {
                _whiteboard.transform.rotation = Quaternion.AngleAxis(180f, Vector3.up) * WhiteboardRot;
                Logger.LogInfo("[Blackout] whiteboard was inside the wall, flipped 180");
            }

            BakeCodeOntoBoard(lod0);
            Logger.LogInfo($"[Blackout] whiteboard spawned at {WhiteboardPos}, raid code {_raidCode}");
            return true;
        }

        // the code is baked into a copy of the board's own texture, so the ink is part of the
        // lit surface - black under any lighting, NVG included, and it cannot show through
        // walls. Board-local -> UV mapping measured from Marker_Board_B_LOD0's mesh:
        //   u = 0.4895 - 0.4334 * localX      (u runs opposite local x)
        //   v = 0.9933 - 0.4313 * (localZ - 0.892)   (v runs top-down; localZ is board height)
        private RenderTexture _boardRt;

        private void BakeCodeOntoBoard(Renderer lod0)
        {
            var atlas = LoadDigitAtlas();
            if (atlas == null || lod0 == null)
            {
                Logger.LogWarning("[Blackout] no digit atlas or LOD0 renderer, code stays log-only");
                return;
            }

            // the writing-surface material is the one sampling the board texture
            Material boardMat = null;
            foreach (var mat in lod0.materials)
            {
                var tex = mat != null ? mat.mainTexture : null;
                Logger.LogInfo($"[Blackout] board material '{(mat != null ? mat.name : "null")}' tex '{(tex != null ? tex.name : "none")}'");
                if (tex != null && tex.name.ToLowerInvariant().Contains("board"))
                {
                    boardMat = mat;
                }
            }
            if (boardMat == null && lod0.materials.Length > 0)
            {
                boardMat = lod0.materials[0];
            }
            if (boardMat == null || boardMat.mainTexture == null)
            {
                Logger.LogWarning("[Blackout] board material/texture not found, code stays log-only");
                return;
            }

            var guiShader = Shader.Find("Hidden/Internal-GUITexture");
            if (guiShader == null)
            {
                Logger.LogWarning("[Blackout] GUI blit shader missing, code stays log-only");
                return;
            }

            var src = boardMat.mainTexture;
            _boardRt = new RenderTexture(src.width, src.height, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(src, _boardRt);

            var blitMat = new Material(guiShader) { mainTexture = atlas };
            var prev = RenderTexture.active;
            RenderTexture.active = _boardRt;
            GL.PushMatrix();
            GL.LoadOrtho();

            // the live board's other markings (tic-tac-toe games, notes) are also drawn at
            // runtime - live ships the same clean texture SPT does. One overlay pass across
            // the writing surface (u 0.0116..0.9673, v 0.3135..0.9933), code area left blank
            var overlay = LoadEmbeddedTexture("Blackout.board_overlay.png");
            if (overlay != null)
            {
                var overlayMat = new Material(guiShader) { mainTexture = overlay };
                overlayMat.SetPass(0);
                GL.Begin(GL.QUADS);
                GL.Color(Color.white);
                GL.TexCoord2(0f, 1f); GL.Vertex3(0.0116f, 0.3135f, 0f);
                GL.TexCoord2(1f, 1f); GL.Vertex3(0.9673f, 0.3135f, 0f);
                GL.TexCoord2(1f, 0f); GL.Vertex3(0.9673f, 0.9933f, 0f);
                GL.TexCoord2(0f, 0f); GL.Vertex3(0.0116f, 0.9933f, 0f);
                GL.End();
            }

            blitMat.SetPass(0);

            // digits at the live event's own decal poses: DecalHint_Paint_00..03 sit at world
            // z -335.04..-334.74 (0.10m pitch, viewer's right half), y ~1.795 - converted to
            // board-local via the same affine as the UV map. Cell aspect 256x341
            const float digitW = 0.10f * 0.4334f;
            const float digitH = 0.133f * 0.4313f;
            const float startX = -0.473f;
            const float stepX = -0.10f;
            const float boardZ = 1.702f;
            GL.Begin(GL.QUADS);
            GL.Color(Color.white);
            for (var i = 0; i < _raidCode.Length; i++)
            {
                var digit = _raidCode[i] - '0';
                var u = 0.4895f - 0.4334f * (startX + stepX * i);
                var v = 0.9933f - 0.4313f * (boardZ - 0.892f);
                // atlas grid: top row 1,2,3,0; middle 4,5,6; bottom 7,8,9
                var col = digit == 0 ? 3 : (digit - 1) % 3;
                var rowIdx = digit == 0 ? 0 : (digit - 1) / 3;
                var au0 = col / 4f;
                var au1 = (col + 1) / 4f;
                var avTop = 1f - rowIdx / 3f;
                var avBottom = 1f - (rowIdx + 1) / 3f;

                // board art is stored v-flipped (v grows toward the board's bottom), so the
                // glyph's atlas v is flipped to land upright on the rendered board
                GL.TexCoord2(au0, avTop); GL.Vertex3(u - digitW / 2f, v - digitH / 2f, 0f);
                GL.TexCoord2(au1, avTop); GL.Vertex3(u + digitW / 2f, v - digitH / 2f, 0f);
                GL.TexCoord2(au1, avBottom); GL.Vertex3(u + digitW / 2f, v + digitH / 2f, 0f);
                GL.TexCoord2(au0, avBottom); GL.Vertex3(u - digitW / 2f, v + digitH / 2f, 0f);
            }
            GL.End();
            GL.PopMatrix();
            RenderTexture.active = prev;

            boardMat.mainTexture = _boardRt;
            Logger.LogInfo($"[Blackout] code baked onto board texture ({src.width}x{src.height})");
        }

        // the live event's own hand-painted digit art: DecalHint_Marker (live sharedassets704),
        // a 4x3 atlas - top row 1,2,3,0 then 4,5,6 and 7,8,9 (last column scribble patches)
        private Texture2D LoadDigitAtlas() => LoadEmbeddedTexture("Blackout.hint_marker.png");

        private static Texture2D LoadEmbeddedTexture(string resource)
        {
            using (var stream = typeof(BlackoutPlugin).Assembly.GetManifestResourceStream(resource))
            {
                if (stream == null)
                {
                    return null;
                }
                var bytes = new byte[stream.Length];
                stream.Read(bytes, 0, bytes.Length);
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!ImageConversion.LoadImage(tex, bytes))
                {
                    return null;
                }
                tex.name = resource;
                return tex;
            }
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

        private bool BlockGateRamps()
        {
            for (var i = 0; i < RampCenters.Length; i++)
            {
                var go = new GameObject("blackout_ramp_block");
                go.transform.position = RampCenters[i];
                var obstacle = go.AddComponent<UnityEngine.AI.NavMeshObstacle>();
                obstacle.shape = UnityEngine.AI.NavMeshObstacleShape.Box;
                obstacle.size = RampSizes[i];
                obstacle.carving = true;
                _rampBlockers.Add(go);
            }
            Logger.LogInfo($"[Blackout] {_rampBlockers.Count} gate ramps carved off the AI navmesh until the switch");
            return true;
        }

        private void UnblockGateRamps()
        {
            if (_rampBlockers.Count == 0)
            {
                _rampsBlocked = false;
                return;
            }
            foreach (var go in _rampBlockers)
            {
                if (go != null)
                {
                    Destroy(go);
                }
            }
            _rampBlockers.Clear();
            _rampsBlocked = false;
            Logger.LogInfo("[Blackout] Gate ramps reopened to AI");
        }

        private void ActivateGates()
        {
            UnblockGateRamps();
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
            Logger.LogInfo($"[Blackout] Bundle loaded: blackout sfx {(_powerDownClip != null ? "ok" : "MISSING")}, announcer {(_announcerClip != null ? "ok" : "MISSING")}");
        }
    }
}
