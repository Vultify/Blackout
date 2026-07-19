# Changelog

All notable changes to Blackout are documented here. Format follows [Keep a Changelog](https://keepachangelog.com/); versions follow [SemVer](https://semver.org/).

## [Unreleased]
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
- The admin office whiteboard from the live event, cloned at the real event pose, with a fresh 4-digit emergency code handwritten on it every raid plus the live board's own markings
### Changed
- Real darkness instead of a camera trick: the map's light scene is dropped the way the live dark preset does it, fixtures and emissive surfaces (sky ceiling, lamp glass, LED panels) go physically dark, the map's own ambient lighting channels are taken over at the source, and the screen-wide exposure cut is gone - flashlights now just work, no compensation needed
