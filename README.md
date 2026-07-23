# Blackout

> **In development - not released.**

Mirrors the live EFT "Blackout" event on The Lab. Seconds into the raid the generators die, the lights go out, and you run the facility on emergency power - while the Wedge and his Black Division hold the floors.

## The blackout

- Real darkness, done the way the live dark preset does it - the light scene drops, fixtures and emissive surfaces go physically dark, flashlights just work
- The live event's power-cut sound and intercom announcement through the map's own PA speakers
- Amber emergency floods at the real event positions, keycard LEDs stay alive like live keeps them
- Extract lockdown: only the two gates are live, and one press of the admin office switch (the event's own switch, at its exact pose) opens both
- Keycard doors take a 4-digit emergency code during the blackout instead of swipes - the code is fresh every raid and written on the admin office whiteboard in the event's own marker digits

## The Wedge and Black Division

- The Wedge spawns every raid with 4-5 guards, a second squad holds the other floor, and Black Division waves sweep in every couple minutes
- His real live loadout: the MP7A1 "Wedge" preset with the suppressor always on, his own face, clothing and voicelines from the live files, the M53A1 gas mask, EXFIL helmet and LV-119 Icebreaker
- SAIN runs their combat when installed (own "Black Division" section in the SAIN menu, soldiers at raider grade, the Wedge at boss grade); without SAIN they fall back to PMC brains
- The Wedge always carries the **TerraGroup Labs arsenal storage room key** - single use, opens the arsenal, and the door can't be breached. Killing him is the way in. His soldiers have a slim chance to carry one too

## Requirements

- SPT 4.0.x
- [WTT-ServerCommonLib](https://forge.sp-tarkov.com/) and WTT-ContentBackport
- MoreBotsAPI
- SAIN recommended, not required

## Structure

- `BlackoutPrepatch/` - prepatcher, registers the Wedge and Black Division spawn types
- `Blackout/` - client BepInEx plugin (darkness, sounds, door locks, keypad)
- `BlackoutServer/` - server mod (items, bots, spawns, the arsenal key)
- `blackout_sounds.bundle` - the live event's audio, extracted and repacked

See [CHANGELOG.md](CHANGELOG.md) for the full build log.
