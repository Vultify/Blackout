using System;
using BepInEx;
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
    // Optional bridge: keeps a co-op raid's blackout in step. Hard depends on both plugins, so on a
    // install without Fika it simply never loads and Blackout carries on deciding everything locally.
    //
    // Three things travel: the moment the lights cut, the admin switch being pulled, and a keycard
    // door being opened with the code. The raid code itself needs nothing here - the server rolls it
    // and every client already reads the same digits off /blackout/state.
    [BepInPlugin("com.vultify.blackout.fika", "Blackout Fika", "1.0.0")]
    // 2.1.0 is where BlackoutSync appeared - against an older Blackout.dll every hook in here is a
    // missing type, so require the floor and let BepInEx skip us cleanly instead
    [BepInDependency("com.vultify.blackout", "2.1.0")]
    [BepInDependency("com.fika.core", BepInDependency.DependencyFlags.HardDependency)]
    public class BlackoutFikaPlugin : BaseUnityPlugin
    {
        private static BlackoutFikaPlugin _instance;
        private bool _registered;
        private float _nextRetry;

        private void Awake()
        {
            _instance = this;
            FikaEventDispatcher.SubscribeEvent<FikaGameCreatedEvent>(OnGameCreated);
            FikaEventDispatcher.SubscribeEvent<FikaGameEndedEvent>(OnGameEnded);

            // outgoing: Blackout tells us something happened here, we put it on the wire
            BlackoutSync.CutHappened += () => Send(new CutPacket());
            BlackoutSync.SwitchFlipped += () => Send(new SwitchPacket());
            BlackoutSync.DoorUnlocked += id => Send(new DoorPacket { DoorHash = BlackoutSync.HashId(id) });

            Logger.LogInfo("[BlackoutFika] loaded, waiting for a raid");
        }

        private void OnGameCreated(FikaGameCreatedEvent e)
        {
            BlackoutSync.Active = true;
            BlackoutSync.IsHost = FikaBackendUtils.IsServer;
            Logger.LogInfo($"[BlackoutFika] raid started, this client is {(BlackoutSync.IsHost ? "HOST" : "CLIENT")}");
            RegisterPackets();
        }

        private void OnGameEnded(FikaGameEndedEvent e)
        {
            BlackoutSync.LeaveRaid();
            _registered = false;
            Logger.LogInfo("[BlackoutFika] raid ended, sync off");
        }

        // Fika's network manager may not exist yet on the frame the raid is created, and a miss there
        // would leave the raid silently unsynced - so keep trying until it takes
        private void Update()
        {
            if (!BlackoutSync.Active || _registered || Time.time < _nextRetry)
            {
                return;
            }
            _nextRetry = Time.time + 1f;
            RegisterPackets();
        }

        // handlers have to be attached once per raid, after the network manager exists
        private void RegisterPackets()
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
                        Logger.LogInfo("[BlackoutFika] CUT received from a client, applying and relaying");
                        BlackoutSync.ApplyCut?.Invoke();
                        Relay(new CutPacket());
                    });
                    server.RegisterPacket<SwitchPacket>(_ =>
                    {
                        Logger.LogInfo("[BlackoutFika] SWITCH received from a client, applying and relaying");
                        BlackoutSync.ApplySwitch?.Invoke();
                        Relay(new SwitchPacket());
                    });
                    server.RegisterPacket<DoorPacket>(p =>
                    {
                        Logger.LogInfo($"[BlackoutFika] DOOR {p.DoorHash} received from a client, applying and relaying");
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
                        Logger.LogInfo("[BlackoutFika] CUT received, killing the lights");
                        BlackoutSync.ApplyCut?.Invoke();
                    });
                    client.RegisterPacket<SwitchPacket>(_ =>
                    {
                        Logger.LogInfo("[BlackoutFika] SWITCH received, opening the gates");
                        BlackoutSync.ApplySwitch?.Invoke();
                    });
                    client.RegisterPacket<DoorPacket>(p =>
                    {
                        Logger.LogInfo($"[BlackoutFika] DOOR {p.DoorHash} received, unlocking");
                        BlackoutSync.ApplyDoorUnlock?.Invoke(p.DoorHash);
                    });
                }
                _registered = true;
                Logger.LogInfo($"[BlackoutFika] packets registered as {(BlackoutSync.IsHost ? "HOST" : "CLIENT")} - this raid IS synced");
            }
            catch (Exception ex)
            {
                Logger.LogError($"[BlackoutFika] could not register packets, this raid runs UNSYNCED: {ex}");
            }
        }

        private static void Send<T>(T packet) where T : INetSerializable
        {
            if (!BlackoutSync.Active)
            {
                return;
            }
            _instance?.Logger.LogInfo($"[BlackoutFika] sending {typeof(T).Name} as {(BlackoutSync.IsHost ? "HOST" : "CLIENT")}");
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
                _instance?.Logger.LogError($"[BlackoutFika] send failed: {ex.Message}");
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
                _instance?.Logger.LogError($"[BlackoutFika] relay failed: {ex.Message}");
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
