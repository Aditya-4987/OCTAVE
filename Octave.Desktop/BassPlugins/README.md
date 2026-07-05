# BASS Decoder Add-ons

The core `bass.dll` only decodes **MP3 / MP2 / MP1 / OGG Vorbis / WAV / AIFF**.
To play the other formats Octave advertises, drop the matching un4seen add-on
DLLs into **this folder**. The build copies any `*.dll` here flat next to
`bass.dll` in the output directory, and `ManagedBassAudioService.LoadPlugins()`
loads them at engine init. Missing plugins are skipped silently (logged to
Debug output), so the app still runs without them.

## Required DLLs (x64, to match the app target)

| File           | Formats unlocked                 | Source (un4seen.com)        |
| -------------- | -------------------------------- | --------------------------- |
| `bassflac.dll` | FLAC                             | BASSFLAC add-on             |
| `bassopus.dll` | Opus (`.opus`)                   | BASSOPUS add-on             |
| `bass_aac.dll` | AAC, M4A, MP4                    | BASS_AAC add-on             |
| `bassalac.dll` | Apple Lossless (ALAC)            | BASSALAC add-on             |
| `basswma.dll`  | WMA                              | BASSWMA add-on              |
| `bassdsd.dll`  | DSD (`.dsf` / `.dff`)            | BASSDSD add-on              |
| `bass_ape.dll` | Monkey's Audio (APE)            | BASS_APE add-on             |

Download each add-on ZIP from https://www.un4seen.com/, extract the **x64**
build of the DLL, and place it here. Use the same architecture as `bass.dll`
(currently x64) or the plugin will fail to load.

## Equalizer add-on

The 10-band equalizer (Settings → Equalizer) needs **`bass_fx.dll`** (the BASS_FX
add-on from un4seen). Drop the x64 `bass_fx.dll` here as well. Unlike the decoder
add-ons above, it is *not* loaded via `PluginLoad` — the `ManagedBass.Fx` API loads
it on demand, so it only needs to sit next to `bass.dll` in the output folder.
Without it, the EQ controls are inert (the engine logs and no-ops).

> The list of plugin filenames the engine attempts to load lives in
> `Octave.Core/Services/Audio/ManagedBassAudioService.cs` (`PluginFileNames`).
