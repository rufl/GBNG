These archives are historical Game Builder builds, not current releases.

To extract the split Steam archive, install 7-Zip (https://www.7-zip.org/),
right-click `gamebuilder-ARCHIVE.7z.001`, choose 7-Zip, then choose an
extraction option.

`gamebuilder-ARCHIVE.7z.001` is the last build published to Steam. Its
multiplayer instructions apply only to that historical binary: it used Photon
PUN and required a PUN App ID.

The current `unity` branch uses Unity Netcode for GameObjects and Unity
Transport for player-hosted multiplayer. It does not require PUN or a paid
Unity networking service. See the root README for current networking and build
instructions.

`gamebuilder-OSS.7z` is an older prebuilt runtime, not a source archive and not
a reproducible build of the current source. To build the current project,
checkout the `unity` branch and use `Game Builder | Build` in the pinned Unity
editor.
