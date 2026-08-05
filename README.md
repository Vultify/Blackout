# Blackout

Mirrors the live EFT "Blackout" event on The Lab. Seconds into the raid the generators die, the lights go out, and you run the facility on emergency power.

## The blackout

- Real darkness, done the way the live dark preset does it - the light scene drops, fixtures and emissive surfaces go physically dark, flashlights just work
- The live event's power-cut sound and intercom announcement through the map's own PA speakers
- Amber emergency floods at the real event positions, keycard LEDs stay alive like live keeps them
- Extract lockdown: only the two gates are live, and one press of the admin office switch (the event's own switch, at its exact pose) opens both
- Keycard doors take a 4-digit emergency code during the blackout instead of swipes - the code is fresh every raid and written on the admin office whiteboard in the event's own marker digits

## The arsenal

The **Admin's Key** spawns on the boss's desk in the manager's office on a blackout raid - single use, opens the arsenal, and the door can't be breached. The manager's office is itself locked, so it's the manager's office key first, then the arsenal.

The Wedge and his Black Division guards used to be part of this mod. They aren't any more - WTT's **Black Division** mod adds the Wedge itself now, so keeping our own meant two of him. Run that alongside Blackout if you want him holding the floors.

## Config

One setting, in `SPT/user/mods/Blackout/config.json`:

```json
{ "blackoutChance": 25 }
```

The percent chance any given Labs raid goes dark. Set it to 100 to get the event every time, or 0 to switch the mod off entirely without uninstalling it. There is no F12 menu - everything else is fixed at the values tuned against the live event.

## Co-op

Fika raids stay in step. The server rolls one emergency code for the raid so everyone reads the same digits off the whiteboard, and the three things that can otherwise drift apart all travel between players - the lights cut for the whole group at once, whoever pulls the admin switch opens the gates for everyone, and a keycard door one player opens with the code is open for the rest.

The bridge that does this ships **inside this download** as of 3.1.0, in `BepInEx/plugins/Blackout/`. It is not a plugin in its own right - Blackout loads it itself, and only once it sees Fika installed - so a solo install ignores the file entirely and there is nothing to configure either way.

There used to be a separate **Blackout Fika** addon. It is discontinued. If you installed it, delete `BepInEx/plugins/BlackoutFika/` - left in place it loads a second copy of the bridge alongside this one.

## Requirements

- SPT 4.0.x
- WTT-CommonLib **2.0.22 or newer** - both halves (WTT-ServerCommonLib and WTT-ClientCommonLib)
- WTT's Black Division mod optional - it brings the Wedge, which this mod no longer does
- Fika optional - co-op raids sync themselves with no extra download, see [Co-op](#co-op)

## Structure

- `Blackout/` - client BepInEx plugin (darkness, sounds, door locks, keypad)
- `BlackoutServer/` - server mod (the raid roll and the Admin's Key)
- `BlackoutFika/` - the co-op bridge, shipped alongside the client plugin
- `blackout_sounds.bundle` - the live event's audio, extracted and repacked

See [CHANGELOG.md](CHANGELOG.md) for the full build log.
