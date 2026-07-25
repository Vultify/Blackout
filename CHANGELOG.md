# Changelog

All notable changes to Blackout are documented here. Format follows [Keep a Changelog](https://keepachangelog.com/); versions follow [SemVer](https://semver.org/).

## [Unreleased]

## [1.0.0] - 2026-07-24
First stable release. Same build the 0.0.5 and 0.0.6 test rounds validated, with no gameplay changes - the beta tags are superseded by this one.
### Changed
- README now matches what the mod actually does: the Wedge holds Labs with 4-5 guards and no waves, and the requirements list names both halves of every dependency

## [0.0.6] - 2026-07-23
### Changed
- F12 menu is now a single Enable Mod toggle; every other setting is fixed at the value tuned against the live event
- The blackout is Labs-only, baked in - it no longer runs on any other map
### Removed
- The debug tools (door-inspector hotkey and the bot-name ESP overlay) are gone
### Fixed
- The client plugin now hard-depends on WTT-CommonLib and WTT-ContentBackport, so a missing client half throws a clear BepInEx error instead of leaving the Wedge's gear and the arsenal key invisible

## [0.0.5] - 2026-07-23
### Added
- Labs goes dark 15 seconds after you start moving - scene lights killed plus a fixed exposure drop, tuned round by round against live event footage
- The live event's real blackout sound and intercom announcement voice, shipped in an asset bundle (runtime-loaded clips decode silent in this client)
- Announcement plays through the map's own PA speaker objects at normalized pitch, with a virtual speaker array fallback for maps without them
- Announcement System text box in the game's Bender font during the intercom voice, text configurable
- Flashlights and gear lights protected from the light kill and compensated against the exposure drop
- The event's dark ambience loop after the cut, volume configurable
- Admin's key recreated 1:1 from the live event (real item id, name and description) via a new server-side component
- First lockdown door: the Labs medical corridor door starts locked and closed, opened by the Admin's key
- Extract lockdown: only Parking Gate and Hangar Gate remain live - every other extract is disabled, its activation consoles dead, and its row hidden from the extract list
- The extraction switch in the admin office, recreated from the live event's own scene data: same trigger position, same boiler control panel on the wall, one press activates both gates
- The live event's amber emergency floods, spawned at the real dark-scene siren positions near the gates, brightness configurable
- Labs' keycard blinkers and door-status LEDs stay alive and script-driven during the blackout, like the live event keeps them
- Ambient Level slider - 0 is live-event black, nudge it up for a hint of visibility
- The admin office whiteboard from the live event, cloned at the real event pose, with a fresh 4-digit emergency code every raid - written in the live event's own hand-painted marker digits, next to the real board markings (the cleaning shift roster and notes), all placed at the live decal positions
- Keycard doors take the emergency code during the blackout: swiping is disabled, the reader opens the live event's keypad (its real panel sprites, layout and bender font, recovered from the live UI files), right code unlocks with the door's own granted beep, wrong code gets the denied beep and the reader's red LED flicker
- The Wedge's gear, backported from live: HK MP7 ARS stock adapter and FAB Defense FX-KPOS stock, SureFire SF3P flash hider that takes the SOCOM556 cans, Team Wendy EXFIL helmets in MultiCam and black, the Wedge's cloth helmet cover, the black ComTac VI headset, and the Spiritus Systems LV-119 Icebreaker plate carrier with its own 15-pouch layout
- The Wedge himself, holding Labs with 4-5 Black Division Guards - our own bot types through MoreBotsAPI, no dependency on the BlackDiv mod. He and his guards are also set friendly toward TacticalToaster's separate BlackDiv mod, so all of them hold Labs together instead of fighting
- The Wedge spawns with his live loadout: the real MP7A1 "Wedge" preset (SF3P + SOCOM556-MINI, the suppressor is forced and can never roll off, Aimpoint T-1, black AN/PEQ-15, flip-up sights, 40-round mags of AP SX), a PVS-31A on his helmet's Wilcox mount, his own head, clothing and voice ripped from the live bundles, the Avon M53A1 gas mask, and no backpack or sidearm - just the MP7
- His pockets always hold the arsenal key, a 3500-5000 euro stack and 1-3 stims; his rig carries an AFAK, a frag and a flashbang, spare mags, and rarely a keycard holder
- His guards run a Black Division kit at raider grade - rifles, plate carriers, helmets with active night vision - off a recovered BlackDiv-style loadout; 4-5 spawn at his side at raid start, no waves
- SAIN drives their combat: the Wedge registers under the Bosses tab at boss grade, his guards under their own "Black Division" tab at raider grade, both with a vanilla PMC brain as the fallback when SAIN isn't installed
- Kill screen reads "Black Div" and the Wedge counts as a real boss kill
- The black AN/PEQ-15 mounts on the MP7 in the gunsmith (Content Backport's clone id was missing from the tactical slot filters)
- Debug toggle: floating role tags over every AI so you can pick the Wedge out of a raid (off by default)
### Changed
- Real darkness instead of a camera trick: the map's light scene is dropped the way the live dark preset does it, fixtures and emissive surfaces (sky ceiling, lamp glass, LED panels) go physically dark, the map's own ambient lighting channels are taken over at the source, and the screen-wide exposure cut is gone - flashlights now just work, no compensation needed
- The event key is now its live self: TerraGroup Labs arsenal storage room key (TGL ASR), single use, and the arsenal door can no longer be breached - the key is the only way in
- The dark ambience loop is gone; the power-cut boom and the intercom announcement stay
### Fixed
- Magenta EXFIL helmets: every shipped bundle now declares its real dependencies, generated from the bundles themselves instead of written by hand
