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
        private const string SoundFileName = "generator_powerdown.wav";
        private const float RescanIntervalSec = 2f;

        private ConfigEntry<bool> _modEnabled;
        private ConfigEntry<bool> _labsOnly;
        private ConfigEntry<float> _delaySeconds;
        private ConfigEntry<float> _darknessStrength;
        private ConfigEntry<float> _soundVolume;

        private bool _inRaid;
        private bool _clockStarted;
        private bool _blackoutActive;
        private float _blackoutAt;
        private float _nextRescan;
        private AudioClip _powerDownClip;
        private bool _errorLogged;

        private readonly List<LightState> _killedLights = new List<LightState>();
        private readonly HashSet<Light> _trackedLights = new HashSet<Light>();
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
                5f,
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

            _soundVolume = Config.Bind(
                "3. Sound",
                "Volume",
                0.8f,
                new ConfigDescription(
                    "Volume of the generator power-down sound",
                    new AcceptableValueRange<float>(0f, 1f)));

            _powerDownClip = LoadPowerDownClip();
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
                    _clockStarted = false;
                    _blackoutActive = false;
                    _killedLights.Clear();
                    _trackedLights.Clear();
                    _originalLightmaps = null;
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
                return;
            }

            if (_labsOnly.Value && gameWorld.LocationId != LabsLocationId)
            {
                return;
            }

            // don't start the clock until the player is actually in control —
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

            _originalAmbientIntensity = RenderSettings.ambientIntensity;
            _originalAmbientLight = RenderSettings.ambientLight;
            _originalReflectionIntensity = RenderSettings.reflectionIntensity;

            // some maps carry baked lightmaps (Labs doesn't — confirmed 0 there)
            _originalLightmaps = LightmapSettings.lightmaps;
            LightmapSettings.lightmaps = new LightmapData[0];

            CreatePpVolume();

            _nextRescan = 0f;
            EnforceBlackout();
            PlayPowerDownSound();
            Logger.LogInfo($"[Blackout] Power cut: {_killedLights.Count} lights killed, {_originalLightmaps.Length} lightmaps cleared, pp volume {(_ppVolume != null ? "on" : "OFF")}");
        }

        private void EnforceBlackout()
        {
            // scene scans are pricey — discover new lights on an interval,
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
                    // leave player/bot gear lights alone — flashlights are the point of a blackout
                    if (light.GetComponentInParent<Player>() != null)
                    {
                        continue;
                    }
                    _trackedLights.Add(light);
                    _killedLights.Add(new LightState { Light = light, Intensity = light.intensity });
                }
            }

            foreach (var state in _killedLights)
            {
                if (state.Light != null && state.Light.enabled)
                {
                    state.Light.enabled = false;
                }
            }

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
            _killedLights.Clear();
            _trackedLights.Clear();

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

        private void PlayPowerDownSound()
        {
            if (_powerDownClip == null || _soundVolume.Value <= 0f)
            {
                return;
            }

            // raw AudioSources are inaudible in-raid — audio must route through EFT's mixer
            var betterAudio = Singleton<BetterAudio>.Instance;
            if (betterAudio != null)
            {
                betterAudio.PlayNonspatial(
                    _powerDownClip,
                    BetterAudio.AudioSourceGroupType.Nonspatial,
                    0f,
                    _soundVolume.Value,
                    null);
                return;
            }

            Logger.LogWarning("[Blackout] BetterAudio unavailable, falling back to raw AudioSource");
            var go = new GameObject("BlackoutPowerDownSound");
            var source = go.AddComponent<AudioSource>();
            source.spatialBlend = 0f; // facility-wide, not positional
            source.volume = _soundVolume.Value;
            source.clip = _powerDownClip;
            source.Play();
            Destroy(go, _powerDownClip.length + 1f);
        }

        private AudioClip LoadPowerDownClip()
        {
            try
            {
                var path = Path.Combine(
                    Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "",
                    "sounds",
                    SoundFileName);
                if (!File.Exists(path))
                {
                    Logger.LogWarning($"[Blackout] Sound file missing, blackout will be silent: {path}");
                    return null;
                }

                var clip = WavLoader.Load(File.ReadAllBytes(path), SoundFileName);
                Logger.LogInfo($"[Blackout] Loaded power-down sound ({clip.length:0.0}s)");
                return clip;
            }
            catch (Exception ex)
            {
                Logger.LogError($"[Blackout] Failed to load sound: {ex}");
                return null;
            }
        }
    }

    // minimal PCM16 WAV reader; avoids pulling in the UnityWebRequest modules just to load one file
    internal static class WavLoader
    {
        public static AudioClip Load(byte[] data, string name)
        {
            int channels = 0, sampleRate = 0, bitsPerSample = 0;
            int dataOffset = -1, dataLength = 0;

            var pos = 12; // skip RIFF header
            while (pos + 8 <= data.Length)
            {
                var chunkId = System.Text.Encoding.ASCII.GetString(data, pos, 4);
                var chunkSize = BitConverter.ToInt32(data, pos + 4);
                if (chunkId == "fmt ")
                {
                    channels = BitConverter.ToInt16(data, pos + 10);
                    sampleRate = BitConverter.ToInt32(data, pos + 12);
                    bitsPerSample = BitConverter.ToInt16(data, pos + 22);
                }
                else if (chunkId == "data")
                {
                    dataOffset = pos + 8;
                    dataLength = chunkSize;
                }
                pos += 8 + chunkSize + (chunkSize & 1);
            }

            if (dataOffset < 0 || channels == 0 || bitsPerSample != 16)
            {
                throw new InvalidDataException($"unsupported wav (channels={channels}, bits={bitsPerSample})");
            }

            var sampleCount = dataLength / 2;
            var samples = new float[sampleCount];
            for (var i = 0; i < sampleCount; i++)
            {
                samples[i] = BitConverter.ToInt16(data, dataOffset + i * 2) / 32768f;
            }

            var clip = AudioClip.Create(name, sampleCount / channels, channels, sampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }
    }
}
