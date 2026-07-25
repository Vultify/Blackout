# Blackout

Mirrors the live EFT "Blackout" event on The Lab. Seconds into the raid the generators die, the lights go out, and you run the facility on emergency power - while the Wedge and his Black Division hold the floors.

## The blackout

- Real darkness, done the way the live dark preset does it - the light scene drops, fixtures and emissive surfaces go physically dark, flashlights just work
- The live event's power-cut sound and intercom announcement through the map's own PA speakers
- Amber emergency floods at the real event positions, keycard LEDs stay alive like live keeps them
- Extract lockdown: only the two gates are live, and one press of the admin office switch (the event's own switch, at its exact pose) opens both
- Keycard doors take a 4-digit emergency code during the blackout instead of swipes - the code is fresh every raid and written on the admin office whiteboard in the event's own marker digits

## The Wedge and Black Division

- The Wedge holds Labs every raid with 4-5 Black Division Guards - no waves, one boss and his escort, our own bot types rather than a dependency on anyone else's
- He and his guards are set friendly toward WTT's separate Black Division mod, so you can run both and let their spawns fill the raid out around him
- His real live loadout: the MP7A1 "Wedge" preset with the suppressor always on, his own face, clothing and voicelines from the live files, the M53A1 gas mask, EXFIL helmet and LV-119 Icebreaker
- SAIN runs their combat when installed (the Wedge under the Bosses tab at boss grade, his guards under their own "Black Division" tab at raider grade); without SAIN they fall back to PMC brains
- The Wedge always carries the **TerraGroup Labs arsenal storage room key** - single use, opens the arsenal, and the door can't be breached. Killing him is the way in

## Requirements

- SPT 4.0.x
- WTT-CommonLib - both halves (WTT-ServerCommonLib and WTT-ClientCommonLib)
- WTT-ContentBackport - both halves (server and client)
- MoreBotsAPI - both halves (server and prepatcher)
- SAIN recommended, not required
- WTT's Black Division mod optional - the Wedge and his guards are friendly toward it

## Structure

- `BlackoutPrepatch/` - prepatcher, registers the Wedge and Black Division spawn types
- `Blackout/` - client BepInEx plugin (darkness, sounds, door locks, keypad)
- `BlackoutServer/` - server mod (items, bots, spawns, the arsenal key)
- `blackout_sounds.bundle` - the live event's audio, extracted and repacked

See [CHANGELOG.md](CHANGELOG.md) for the full build log.
