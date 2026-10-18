# Opening files and editing items / quests

Use **File → Open…** or **Ctrl+O**. Crucible identifies binary signatures first,
then uses format metadata or the extension to choose an editor.

| File | Opens in |
| --- | --- |
| DBC / supported DB2 | Client table editor |
| M2 / WMO | Model browser / native asset tools |
| BLP, PNG, JPEG, BMP, TGA | Texture editor |
| ADT, WDT, WDL | Maps & World |
| WDB / ADB | Client cache editor |
| MPQ | Archive editor |
| SQL | SQL query editor; opening never executes the script |
| Crucible project JSON | Projects |
| Portable quest JSON | Quest editor, including starter/ender links |
| Class Studio class/workspace JSON | Class Studio |
| CSV or other structured JSON | Destination chooser, followed by the existing import review |

CSV/JSON rows do not uniquely identify a database or client table. Choose an open
client table, browse for its file, or select a connected database table for CSV.
Import validation and confirmation remain in place. Unsupported files report the
problem without modifying the source.

## Items

Choose **Open existing item…** at the top of the item editor. Select a realm,
search by item name or exact ID, and double-click a result. Empty search browses
items in pages of 50. **Save changes** updates the loaded item; its SQL export is
also an UPDATE. Unmapped/custom columns are preserved. Switching realms blocks
saving an item loaded from the previous database until you return or reopen it.
**New item** clears the draft and returns to create mode.

Item class controls the subtype list; subtype controls the equipment slots.
Armor never offers weapon slots. The shared WotLK catalog also supplies subtype
names in Item.dbc and uses **#e6cc80** for both Artifact and Heirloom quality.

Weapon damage/delay, equipment fields and the stats tab appear when applicable.
Existing nonstandard values remain visible and editable, with an explanation on
the field label, rather than being erased on load. Unknown choices retain their
stored numeric value. SQL export and existing guarded deployment still use the
same item adapter.

## Quests

All **105 stock CoA quest_template columns** have a readable label and explanation.
Completion behavior, quest category and XP tier have named choices. Behavior
flags and allowed races have checkboxes; advanced stored values remain available.
Editing known flags/races preserves custom bits.

References have name searches for items, spells, quests, creatures, gameobjects,
factions, areas, quest sorts, maps and titles. Client references use the configured
DBC folder. Creature/object and area/special-category pickers encode the required
positive or negative ID automatically. Counts do not offer ID pickers.

Money and time show their units. Reputation override explains CoA's hundredths
unit: 25000 represents 250 base reputation. QuestType is completion behavior;
QuestInfoID is the normal/group/dungeon/raid category. Unknown0 is explicitly
identified as unused by the CoA quest loader, not assigned an invented meaning.
Custom schema extensions remain accessible and are explicitly unidentified until
their implementation is documented.

## Definition sources

The forms use versioned definitions in Core, not live web scraping. Crucible's
separate offline knowledge search still indexes a user-selected Markdown wiki.

Definitions were checked against the CoA checkout's `ItemTemplate.h`,
`QuestDef.h/.cpp`, `Player.cpp`, `SharedDefines.h`, `DBCStructure.h`, `DBCfmt.h`,
and `data/sql/base/db_world/quest_template.sql`, alongside the
[AzerothCore quest reference](https://www.azerothcore.org/wiki/quest_template) and
[TrinityCore 3.3.5 item reference](https://trinitycore.info/database/335/world/item_template).
The running core's behavior takes precedence over generic documentation.

This is complete coverage of the stock quest_template fields, not a claim that
every field in every WoW table or custom server extension has been documented.
