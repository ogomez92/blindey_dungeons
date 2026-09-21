# Installer

`DiceyDungeonsAccessibilityInstaller.exe` is the one-click installer attached to every GitHub release. It finds
the game through Steam (registry, then `libraryfolders.vdf`, with a Browse fallback), downloads the latest release
zip, extracts it into the game folder (everything lands in `DiceyA11y\`), verifies every file landed, and then
does the one thing special to this mod: it keeps the game's `lime.ndll` as `lime_orig.dll` and puts the bridge in
its place. Uninstall puts the original back and removes `DiceyA11y\`. It is screen reader accessible, and `--cli`
gives a terminal flow.

The swap follows the rules of `tools/install_bridge.ps1` (see CLAUDE.md §7): the bridge is recognised by its
`diceya11y_bridge_marker` export, and a file that is the bridge, or is under 3 MB, is never saved as "the original".

It is built from the shared installer codebase of the accessibility mods (`mods\installer`; the `Dicey Dungeons`
entry in `src/games.rs`, `src/core/proxy.rs` for the swap, `src/bin/dicey.rs` as the entry point). The exe is
committed here so the release workflow can attach it without a manual upload: rebuild it with
`build_installer.bat` in that folder and copy the new exe over this one. The installer reads the release list at
run time, so it rarely needs rebuilding.
