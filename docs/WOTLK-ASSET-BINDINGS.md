# How WotLK models choose textures

WoW does not choose a BLP because its filename resembles an M2 filename. The client resolves exact paths and data-table bindings. A differently named texture can be the correct texture, while an attractively named neighboring BLP can be completely unrelated.

## M2 and SKIN

- An M2 contains vertices, animation data, texture definitions, lookup tables, and render state.
- A companion `Model00.skin` contains the view's triangle/index data, submeshes (geosets), and material units. Other numbered SKIN files are alternate views or LODs.
- A SKIN is not a skin image. It connects visible geometry/material batches to the M2's definitions.
- An ordinary M2 texture definition contains an exact client-relative BLP path. That path is authoritative.
- A replaceable M2 texture definition intentionally contains no fixed BLP. Its type tells the client which appearance system must supply it.

Common Wrath replaceable types include character body/clothes, cape, hair/beard, fur, and the three creature texture-variation slots. Crucible reports these as external bindings rather than guessing a nearby file.

## Creatures

`CreatureModelData.dbc` identifies the M2 path. `CreatureDisplayInfo.dbc` points to that model record and supplies up to three `TextureVariation` values. A creature variation is commonly a filename relative to the model's directory, so its text does not need to match the model filename.

The three values are alternative display skins, not three images that should automatically be stacked. A server creature template chooses a display ID; that display and the model's replaceable texture slots determine the effective appearance.

## Playable characters

Playable bodies are composited appearances rather than one complete BLP:

- `CharSections.dbc` selects base skin, face, facial-hair, underwear, and scalp components by race, sex, section, variation, and color.
- `CharHairGeosets.dbc` and `CharacterFacialHairStyles.dbc` select compatible geometry variants.
- Item display data adds equipment textures and geosets.

Files with upper/lower, variation, color, or numbered names can therefore be atlas components. Similar names do not mean duplicates, and every component should not be rendered at once.

## Items

`ItemDisplayInfo.dbc` supplies item model paths, component textures, geoset groups, icon data, and visual-effect references. The server's `item_template.displayid` selects that client display. The displayed item name is irrelevant to asset binding.

## World models and terrain

- A WMO root stores exact texture paths in `MOTX`; `MOMT` materials reference entries in that string table.
- An ADT stores exact terrain texture paths in `MTEX`; each cell's `MCLY` records select those entries and `MCAL` supplies blending alpha.
- ADT `MMDX/MMID/MDDF` and `MWMO/MWID/MODF` records resolve placed M2 and WMO assets.

## Safe resolution rule

Start from the consumer—M2, WMO, ADT, DBC display row, or SQL display reference—and follow its exact dependency. Preserve provenance while resolving it. Do not pair files by approximate filename, do not combine all numbered variants, and do not borrow a missing dependency from another patch layer unless that cross-layer choice is explicit and reviewed.

Crucible's dependency graph, creature appearance resolver, character appearance renderer, item display resolver, terrain material reader, and texture-consumer index follow these relationships.
