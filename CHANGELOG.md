# Changelog

All notable changes to Blackout are documented here. Format follows [Keep a Changelog](https://keepachangelog.com/); versions follow [SemVer](https://semver.org/).

## [Unreleased]

## [4.0.0] - 2026-08-08
### Changed
- **Ported to SPT 4.1.** This version needs SPT **4.1.x** and will not run on 4.0.13 - stay on 3.1.1 for that. Nothing about the event changed; it is the same mod against a moved API.
- Requires WTT-CommonLib **3.0.0 or newer** (the 4.1 line). The 2.0.x releases are for SPT 4.0 and will not work here.

## [3.1.1] - 2026-08-06
### Fixed
- **A Fika raid hosted by a headless client never went dark.** Fika elects the headless as host, and Blackout gave the host ownership of the moment the lights cut - but that moment is triggered by the host player moving, and a headless has no player. Every client sat waiting for a cut packet that could not come, so the raid ran as normal Labs. Everything else worked, which is why it looked like only the darkness was broken. On a headless-hosted raid the clients now run that clock themselves and the first one there sends the cut, which the headless relays to the rest - so the lights still go out for everyone at once. Player-hosted raids are unchanged.

## [3.1.0] - 2026-08-03
### Changed
- **The Fika addon is now part of this download.** `BlackoutFika.dll` sits in `BepInEx/plugins/Blackout/` and Blackout loads it itself once it sees Fika installed. It is not a plugin in its own right, so a solo install ignores the file completely: no second download to find, and no "1 plugin failed to load" notice for anyone playing without Fika. **If you have the separate Blackout Fika addon installed, delete its `BepInEx/plugins/BlackoutFika/` folder** - left in place it loads a second copy of the bridge alongside this one.
- The event key is now the **Admin's Key** (short name "Admin"), which is what it should have been called all along. Same key, same item, same single use - only the name and description changed, so anything already sitting in your stash is unaffected.
- The darkening pass now cuts specular on the map's surfaces as well as their emission. Labs' most common shader carries no emission at all, so it was skipped entirely, and its surfaces kept catching light out at distance where a flashlight no longer reaches.

## [3.0.0] - 2026-08-02
### Removed
- **The Wedge and his Black Division guards.** WTT's Black Division mod added the Wedge itself recently, so running both meant two of him. Blackout no longer spawns either — install Black Division alongside it if you want him holding the floors.
- **Two dependencies, with them.** WTT-ContentBackport and MoreBotsAPI were only ever there for Wedge's gear and his bot type, and are no longer required. The prepatcher is gone too, so there is nothing left to install in `BepInEx/patchers/`.
### Changed
- The arsenal key now spawns on the boss's desk in the manager's office instead of being carried by the Wedge. The manager's office is locked in its own right, so the arsenal is two keys deep rather than a boss drop. Still single use, still the only way in.
- Raid setup polls four times a second instead of running every frame — the old path ran two `GameObject.Find` calls plus a reflection walk per frame for the whole Labs load.

## [2.1.1] - 2026-07-29
### Fixed
- The server room racks no longer vanish when the lights cut. Labs' light scene holds solid props as well as light fixtures, and the darkening pass was switching whole rooms off to reach the lights inside them. It now keeps anything carrying a collision mesh and darkens it where it stands, the same way the parking vehicles were already handled. The vent modules, steam pipes and desk lamps were disappearing for the same reason and come back with it
### Changed
- `blackoutChance` is re-read before every roll instead of once at server start, so editing `config.json` takes effect on your next raid without restarting the server

## [2.1.0] - 2026-07-27
### Added
- **Fika co-op support.** A blackout raid now runs the same for everyone in it: the lights cut for the whole group at once, the admin office switch opens the gates for every player and not just the one who pulled it, and a keycard door someone else opens with the code is open for you too
- The bridge that does it ships as a **separate Blackout Fika addon**, not in this download, so a solo install never carries a file it cannot use. It needs Blackout 2.1.0 or newer and there is nothing to configure either way
### Changed
- The emergency code is rolled by the server instead of by each client, so every player in a raid reads the same four digits off the same whiteboard. Before this everyone rolled their own, which is invisible alone and useless in a group - each keypad only took the code that player happened to generate
- The server log is down to two lines. It used to announce every check that passed; now a clean start is quiet and each raid gets one line saying whether it rolled a blackout - including the successful roll, which used to be the only outcome that said nothing. Every failure still prints in full
### Removed
- The F12 master toggle. `blackoutChance` in `config.json` already covers it - set it to 0 and no raid ever rolls the event, which is the same thing with one place to look instead of two

## [2.0.0] - 2026-07-25
### Changed
- Wedge's gear, face, clothing and voice now come from WTT Content Backport instead of being shipped here. It backported the same live items in its 1.1.0, and two mods adding the same things meant duplicate items in the handbook and a pile of errors on every server start
- **Content Backport 1.1.0 and CommonLib 2.0.22 are now the minimum versions.** Older ones leave Wedge with no gear at all, so both halves of the mod now refuse to load against an older one instead of failing quietly
### Removed
- Every asset bundle. The download drops from about 61 MB to around 1 MB, since Content Backport ships the same files
### Fixed
- The two "ItemId already exists" errors and the thirteen bundle warnings that appeared when Content Backport 1.1.0 landed
- Wedge's helmet cover mounts again - Content Backport adds the cover slot to the normal black Team Wendy, so the repacked helmet that used to host it is gone

## [1.1.0] - 2026-07-25
### Added
- The blackout is a per raid roll now instead of firing on every Labs run, so you never know which one goes dark until the lights cut. 25% by default, set `blackoutChance` in config.json to change it

### Changed
- The LV-119's soft armor inserts are built in and can no longer be left out, the same as every other armor in the game

### Fixed
- The Wedge's armor and helmet now spawn with their soft armor inserts, not just the plates
- The vehicles at the hangar and parking gates no longer disappear when the lights go out, taking their cover with them

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

---

# Blackout Fika (addon)

The co-op bridge is a separate download with its own version line, so it only moves when the bridge itself changes. It always requires the Blackout version named below or newer.

## [1.0.0] - 2026-07-27
### Added
- First release. Keeps a Fika raid's blackout in step across every player: the host's power cut takes the whole group dark at once, whoever pulls the admin office switch opens the gates for everyone, and a keycard door one player opens with the emergency code is open for the rest
- Requires Blackout 2.1.0 or newer. Against anything older it is skipped at load instead of half working, since the hooks it binds to did not exist yet
