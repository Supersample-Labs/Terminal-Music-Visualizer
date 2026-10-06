# Terminal Music Visualizer

A terminal-based music player and ASCII visualizer built with C# and .NET 8. Play local audio files, manage a song queue, and watch colored bars animate directly in your terminal.

## Features and platform support

- **Windows:** music playback through NAudio, live FFT spectrum analysis, audio-driven effects, playback progress, pause/resume, seeking, and volume adjustment.
- **macOS:** music playback through Apple's built-in `afplay`, elapsed-time display, and a decorative animated spectrum. Its animation is not based on audio analysis.
- Queue multiple songs at launch or add files while playing by dragging/pasting paths and pressing Enter.
- Preview upcoming tracks, skip songs, and go back to previous tracks.
- The player stays open after the queue ends so you can add more music or replay a previous track.

Linux playback is not implemented.

## Requirements

- Windows or macOS with an interactive terminal that supports colors and cursor positioning.
- .NET 8 SDK, or a newer SDK capable of targeting .NET 8, to build from source.
- The .NET 8 runtime for framework-dependent builds. Self-contained builds bundle the runtime.
- Local audio files. Windows formats depend on NAudio and installed codecs; MP3, WAV, and M4A commonly work. macOS formats depend on `afplay`.
- A terminal at least 52 columns wide and 20 rows tall is recommended.

## Run from source

Open the directory containing `MusicVisualizer.csproj`:

```powershell
dotnet restore
dotnet run -- "C:\Music\song.mp3" "C:\Music\another song.m4a"
```

On macOS:

```bash
dotnet run -- "/Users/you/Music/song.mp3" "/Users/you/Music/another song.m4a"
```

To start with a file-entry prompt:

```powershell
dotnet run
```

Drag files into the terminal or paste their paths, then press Enter. Use double quotes around paths containing spaces. If macOS Terminal inserts backslash-escaped spaces, replace those with a double-quoted path.

While playing, drag/paste more paths and press Enter to append them to the queue. The footer previews pending filenames. Press `A` to use the explicit add-files prompt.

Show command-line help with `dotnet run -- --help`. The built-in help lists Windows controls; macOS support is shown below.

## Controls

| Key | Windows | macOS |
| --- | --- | --- |
| Space | Pause / resume | Not implemented |
| Left / Right | Seek backward / forward 5 seconds | Not implemented |
| Up / Down | Raise / lower volume by 5 percentage points | Not implemented |
| N | Next song | Next song |
| P | Previous song; replay a previous track after the queue ends | Same |
| A | Prompt for more files | Same |
| Enter | Add buffered file paths | Same |
| Backspace | Edit buffered file paths | Same |
| Esc | Clear pending path input; otherwise quit | Same |
| Q | Quit | Quit |

Windows playback volume starts at 85%. On macOS, use system volume controls. Single-letter shortcuts apply when no file path is being entered.

## Build and publish

```powershell
dotnet build -c Release
```

Create a self-contained, single-file Windows executable:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Run `bin\Release\net8.0\win-x64\publish\musicviz.exe`, optionally followed by quoted audio paths.

For macOS, choose the target processor:

```bash
# Apple Silicon
dotnet publish -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true
# Intel
dotnet publish -c Release -r osx-x64 --self-contained true -p:PublishSingleFile=true
```

The executables are in `bin/Release/net8.0/<runtime>/publish/musicviz`. On the target Mac, open the publish directory and run:

```bash
chmod +x musicviz
./musicviz "/Users/you/Music/song.mp3"
```

## How it works

`Program.cs` contains the entry point, playback backends, terminal rendering, controls, and file-path parsing. Windows uses NAudio 2.2.1 with `AudioFileReader` and `WaveOutEvent`. The analyzer mixes audio to mono for a 2,048-sample FFT, smooths frequency magnitudes, and measures audio levels for visual effects. Playback retains the source audio channels.

macOS launches `afplay` for each track. Its spectrum is animated using sine waves. Pause, seeking, and in-app volume adjustment are currently unavailable on macOS.

## Troubleshooting

- **Missing files:** supply existing local paths. Missing files are reported and skipped.
- **Playback fails:** try a known-good MP3 or WAV and check the system audio output. Files are not validated as playable audio before playback.
- **Display wraps or looks garbled:** enlarge the terminal and run in an interactive console rather than redirecting output.
- **Paths with spaces are split:** double-quote each complete path.
- **Player stays open after the last song:** this is intentional. Add more files, press `P`, or quit with `Q` / `Esc`.

## Project files

- `MusicVisualizer.csproj` — .NET 8 executable and NAudio dependency.
- `Program.cs` — music player, visualizer, controls, and queue handling.
- `.gitignore` — excludes build output, editor state, and operating-system metadata.

## License

Licensed under the [MIT License](LICENSE). Copyright (c) 2026 Supersample Labs.

