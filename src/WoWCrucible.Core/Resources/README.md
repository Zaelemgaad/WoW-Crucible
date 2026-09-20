# Animation Names

`AnimationNames.csv` contains the numeric IDs and names from AnimationData,
sourced from WoW Model Viewer's `bin_support/wow/12.0/AnimationData.csv`.
These are data labels, not copied renderer or application code. Including modern
IDs lets the loose-file browser label animations in both legacy and modern M2s.
The embedded lookup works offline; it is not an additional runtime dependency.
Unknown IDs remain explicitly labelled as unknown instead of being guessed.
