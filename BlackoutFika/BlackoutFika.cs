using System;
using BepInEx.Logging;
using Blackout;
using Comfort.Common;
using Fika.Core.Main.Utils;
using Fika.Core.Modding;
using Fika.Core.Modding.Events;
using Fika.Core.Networking;
using Fika.Core.Networking.LiteNetLib;
using UnityEngine;
using Fika.Core.Networking.LiteNetLib.Utils;

namespace BlackoutFika
{
    // Optional bridge: keeps a co-op raid's blackout in step.
    //
    // Deliberately NOT a BepInEx plugin - no [BepInPlugin], no BaseUnityPlugin. It ships in the same
    // folder as Blackout.dll and the main plugin loads it by hand once it sees Fika in the
    // chainloader. As a plugin it would need either its own download or a hard Fika dependency,
    // and the latter puts "1 plugin failed to load" on screen for everyone playing solo. BepInEx
    // scans this DLL with Cecil, finds no plugin type, and skips it without ever resolving
    // Fika.Core - which is what makes it safe to ship to people who don't have Fika.
    //
    // Three things travel: the moment the lights cut, the admin switch being pulled, and a keycard
    // door being opened with the code. The raid code itself needs nothing here - the server rolls it
    // and every client already reads the same digits off /blackout/state.
    public static class BlackoutFikaBridge
    {
        private static ManualLogSource _log;
        private static bool _initialized;
        private static bool _registered;
        private static float _nextRetry;

        // entry point, invoked by BlackoutPlugin through reflection so the main assembly never
        // names a Fika type and never needs Fika.Core present to load
        public static void Initialize(ManualLogSource log)
        {
            if (_initialized)
            {
                return;
            }
            _log = log;

            FikaEventDispatcher.SubscribeEvent<FikaGameCreatedEvent>(OnGameCreated);
            FikaEventDispatcher.SubscribeEvent<FikaGameEndedEvent>(OnGameEnded);

            // outgoing: Blackout tells us something happened here, we put it on the wire
            BlackoutSync.CutHappened += OnCutHappened;
            BlackoutSync.SwitchFlipped += OnSwitchFlipped;
            BlackoutSync.DoorUnlocked += OnDoorUnlocked;
            // we have no MonoBehaviour of our own any more, so the main plugin drives the
            // registration retry from the Update it already runs
            BlackoutSync.Tick = Tick;

            _initialized = true;
            _log?.LogInfo("[BlackoutFika] bridge attached, waiting for a raid");
        }

        public static void Shutdown()
        {
            if (!_initialized)
            {
                return;
            }

            FikaEventDispatcher.UnsubscribeEvent<FikaGameCreatedEvent>(OnGameCreated);
            FikaEventDispatcher.UnsubscribeEvent<FikaGameEndedEvent>(OnGameEnded);

            BlackoutSync.CutHappened -= OnCutHappened;
            BlackoutSync.SwitchFlipped -= OnSwitchFlipped;
            BlackoutSync.DoorUnlocked -= OnDoorUnlocked;
            BlackoutSync.Tick = null;

            BlackoutSync.LeaveRaid();
            _registered = false;
            _initialized = false;
            _log?.LogInfo("[BlackoutFika] bridge detached");
        }

        private static void OnCutHappened()
        {
            Send(new CutPacket());
        }

        private static void OnSwitchFlipped()
        {
            Send(new SwitchPacket());
        }

        private static void OnDoorUnlocked(string id)
        {
            Send(new DoorPacket { DoorHash = BlackoutSync.HashId(id) });
        }

        private static void OnGameCreated(FikaGameCreatedEvent e)
        {
            BlackoutSync.Active = true;
            BlackoutSync.IsHost = FikaBackendUtils.IsServer;
            // IsHeadlessGame is set from the host query when a client connects, so clients know;
            // IsHeadless covers the headless itself. Either way the host has no player and can
            // never reach its own cut moment, so clients stop waiting on it
            BlackoutSync.HeadlessHost = FikaBackendUtils.IsHeadlessGame || FikaBackendUtils.IsHeadless;
            _log?.LogInfo($"[BlackoutFika] raid started, this client is {(BlackoutSync.IsHost ? "HOST" : "CLIENT")}, headless host: {BlackoutSync.HeadlessHost}");
            RegisterPackets();
        }

        private static void OnGameEnded(FikaGameEndedEvent e)
        {
            BlackoutSync.LeaveRaid();
            _registered = false;
            _log?.LogInfo("[BlackoutFika] raid ended, sync off");
        }

        // Fika's network manager may not exist yet on the frame the raid is created, and a miss there
        // would leave the raid silently unsynced - so keep trying until it takes
        private static void Tick()
        {
            if (!BlackoutSync.Active || _registered || Time.time < _nextRetry)
            {
                return;
            }
            _nextRetry = Time.time + 1f;
            RegisterPackets();
        }

        // handlers have to be attached once per raid, after the network manager exists
        private static void RegisterPackets()
        {
            if (_registered)
            {
                return;
            }
            try
            {
                if (BlackoutSync.IsHost)
                {
                    var server = Singleton<FikaServer>.Instance;
                    if (server == null)
                    {
                        return;
                    }
                    // a client did it: apply it here, then pass it on to everyone else, since
                    // clients only ever talk to the host
                    server.RegisterPacket<CutPacket>(_ =>
                    {
                        _log?.LogInfo("[BlackoutFika] CUT received from a client, applying and relaying");
                        BlackoutSync.ApplyCut?.Invoke();
                        Relay(new CutPacket());
                    });
                    server.RegisterPacket<SwitchPacket>(_ =>
                    {
                        _log?.LogInfo("[BlackoutFika] SWITCH received from a client, applying and relaying");
                        BlackoutSync.ApplySwitch?.Invoke();
                        Relay(new SwitchPacket());
                    });
                    server.RegisterPacket<DoorPacket>(p =>
                    {
                        _log?.LogInfo($"[BlackoutFika] DOOR {p.DoorHash} received from a client, applying and relaying");
                        BlackoutSync.ApplyDoorUnlock?.Invoke(p.DoorHash);
                        Relay(new DoorPacket { DoorHash = p.DoorHash });
                    });
                }
                else
                {
                    var client = Singleton<FikaClient>.Instance;
                    if (client == null)
                    {
                        return;
                    }
                    client.RegisterPacket<CutPacket>(_ =>
                    {
                        _log?.LogInfo("[BlackoutFika] CUT received, killing the lights");
                        BlackoutSync.ApplyCut?.Invoke();
                    });
                    client.RegisterPacket<SwitchPacket>(_ =>
                    {
                        _log?.LogInfo("[BlackoutFika] SWITCH received, opening the gates");
                        BlackoutSync.ApplySwitch?.Invoke();
                    });
                    client.RegisterPacket<DoorPacket>(p =>
                    {
                        _log?.LogInfo($"[BlackoutFika] DOOR {p.DoorHash} received, unlocking");
                        BlackoutSync.ApplyDoorUnlock?.Invoke(p.DoorHash);
                    });
                }
                _registered = true;
                _log?.LogInfo($"[BlackoutFika] packets registered as {(BlackoutSync.IsHost ? "HOST" : "CLIENT")} - this raid IS synced");
            }
            catch (Exception ex)
            {
                _log?.LogError($"[BlackoutFika] could not register packets, this raid runs UNSYNCED: {ex}");
            }
        }

        private static void Send<T>(T packet) where T : INetSerializable
        {
            if (!BlackoutSync.Active)
            {
                return;
            }
            _log?.LogInfo($"[BlackoutFika] sending {typeof(T).Name} as {(BlackoutSync.IsHost ? "HOST" : "CLIENT")}");
            try
            {
                if (BlackoutSync.IsHost)
                {
                    Singleton<FikaServer>.Instance?.SendData(ref packet, DeliveryMethod.ReliableOrdered, true);
                }
                else
                {
                    Singleton<FikaClient>.Instance?.SendData(ref packet, DeliveryMethod.ReliableOrdered);
                }
            }
            catch (Exception ex)
            {
                _log?.LogError($"[BlackoutFika] send failed: {ex.Message}");
            }
        }

        // host only - push a client's event back out to the rest of the raid
        private static void Relay<T>(T packet) where T : INetSerializable
        {
            try
            {
                Singleton<FikaServer>.Instance?.SendData(ref packet, DeliveryMethod.ReliableOrdered, true);
            }
            catch (Exception ex)
            {
                _log?.LogError($"[BlackoutFika] relay failed: {ex.Message}");
            }
        }
    }

    // the lights just went out on the host
    public class CutPacket : INetSerializable
    {
        public void Serialize(NetDataWriter writer) { }
        public void Deserialize(NetDataReader reader) { }
    }

    // someone pulled the admin office switch
    public class SwitchPacket : INetSerializable
    {
        public void Serialize(NetDataWriter writer) { }
        public void Deserialize(NetDataReader reader) { }
    }

    // someone typed the code into a keycard door. Carries a hash of the door id, not the id -
    // see BlackoutSync.HashId for why
    public class DoorPacket : INetSerializable
    {
        public int DoorHash;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(DoorHash);
        }

        public void Deserialize(NetDataReader reader)
        {
            DoorHash = reader.GetInt();
        }
    }
}
