using System.Diagnostics;
using System.Globalization;
using System.Text;
using NAudio.Dsp;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

// Program entry point.
// This top-level code handles command-line help, collects initial song paths,
// chooses the right player implementation for the operating system, and makes
// sure the console is restored if anything throws an exception.
if (args.Length > 0 && args[0] is "-h" or "--help")
{
    ShowHelp();
    return 0;
}

var paths = PathTools.GetAudioPaths(args);

try
{
    // macOS does not use NAudio playback here. It uses the built-in `afplay`
    // command through MacTerminalMusicPlayer so the published app can run in
    // a normal macOS terminal.
    if (OperatingSystem.IsMacOS())
    {
        using var player = new MacTerminalMusicPlayer(paths);
        player.Run();
    }
    else
    {
        // Windows and other non-macOS runs use the NAudio-backed player. On
        // Windows this gives access to decoded audio samples for the real FFT
        // spectrum visualizer.
        using var player = new TerminalMusicPlayer(paths);
        player.Run();
    }

    return 0;
}
catch (Exception ex)
{
    // If playback or drawing fails, restore the terminal so the user's cursor
    // and colors are not left in the visualizer state.
    Console.ResetColor();
    Console.CursorVisible = true;
    Console.Error.WriteLine(ex.Message);
    return 1;
}

static void ShowHelp()
{
    // Prints the basic run syntax and keyboard controls.
    Console.WriteLine("musicviz - retro terminal music player");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run -- \"C:\\Music\\song.mp3\" \"C:\\Music\\song2.m4a\"");
    Console.WriteLine("  musicviz.exe");
    Console.WriteLine();
    Console.WriteLine("Controls:");
    Console.WriteLine("  Space  pause/resume");
    Console.WriteLine("  Left   rewind 5 seconds");
    Console.WriteLine("  Right  skip forward 5 seconds");
    Console.WriteLine("  N      next song");
    Console.WriteLine("  P      previous song");
    Console.WriteLine("  A      add more songs to queue");
    Console.WriteLine("  Drag files anytime, then press Enter to queue them");
    Console.WriteLine("  Up     volume up");
    Console.WriteLine("  Down   volume down");
    Console.WriteLine("  Q/Esc  quit");
}

// Windows/non-macOS terminal player.
// This version uses NAudio to decode audio, play it through WaveOutEvent, and
// feed live samples into AnalyzingSampleProvider for the reactive visualizer.
sealed class TerminalMusicPlayer : IDisposable
{
    // Song queue. Each string is a full path to a local audio file.
    private readonly List<string> queue;

    // Shared clock used to animate scan lines, pulses, and waveform motion.
    private readonly Stopwatch frameClock = Stopwatch.StartNew();

    // NAudio reader for the currently playing file.
    private AudioFileReader? reader;

    // Sample analyzer wrapped around the reader. It produces spectrum bars and
    // level data used by DrawFrame.
    private AnalyzingSampleProvider? analyzer;

    // NAudio output device for playback.
    private WaveOutEvent? output;

    // Index of the currently selected/playing queue item.
    private int currentIndex;

    // Playback volume. Kept separately so the setting carries across tracks.
    private float volume = 0.85f;

    // When a user drags files into a terminal while music is playing, the paths
    // arrive as ordinary typed characters. This buffer captures those characters
    // until Enter is pressed, preventing letters in file paths from triggering
    // hotkeys like Q or N.
    private readonly StringBuilder inputBuffer = new();

    // Footer message shown when there is no active dragged-path buffer.
    private string statusMessage = "Drag files anytime, then press Enter to queue them.";

    // Main loop flags.
    private bool quit;
    private bool skipTrack;
    private bool disposed;

    public TerminalMusicPlayer(List<string> paths)
    {
        // The queue list is intentionally shared and mutable because songs can
        // be appended while playback is running.
        queue = paths;
    }

    public void Run()
    {
        // Make the terminal ready for full-screen-ish drawing.
        Console.OutputEncoding = Encoding.UTF8;
        Console.CursorVisible = false;
        Console.Clear();

        // Keep the application alive until Q/Esc is pressed. If the queue runs
        // out, draw a stopped/idle screen instead of exiting.
        while (!quit)
        {
            if (currentIndex < queue.Count)
            {
                PlayCurrentTrack();

                // A deliberate skip and a natural end both advance the queue by
                // one. Previous-track modifies currentIndex before setting
                // skipTrack, so this increment lands on the intended item.
                if (!quit && skipTrack)
                {
                    skipTrack = false;
                    currentIndex++;
                }
                else if (!quit)
                {
                    currentIndex++;
                }
            }
            else
            {
                // End-of-queue state. The app stays open, waits for input, and
                // redraws slowly because there is no music to animate.
                DisposeCurrentTrack();
                DrawIdleFrame();
                HandleInput();
                Thread.Sleep(50);
            }
        }
    }

    private void PlayCurrentTrack()
    {
        // Tear down the previous track before opening a new decoder/output.
        DisposeCurrentTrack();

        // AudioFileReader decodes common Windows-supported formats and exposes
        // floating-point samples. MeteringSampleProvider sets a smaller read
        // chunk, which helps the visualizer feel responsive.
        reader = new AudioFileReader(queue[currentIndex]) { Volume = volume };
        var meteringProvider = new MeteringSampleProvider(reader, 512);
        analyzer = new AnalyzingSampleProvider(meteringProvider, reader.WaveFormat.Channels);

        // Lower latency makes skip/pause feel snappy in the terminal.
        output = new WaveOutEvent { DesiredLatency = 80 };
        output.Init(analyzer);
        output.Play();

        // Playback loop for one track. It handles keyboard input, redraws the
        // visualizer, and leaves when the song ends, the user skips, or the app
        // is quitting.
        while (!quit && !skipTrack && output.PlaybackState != PlaybackState.Stopped)
        {
            HandleInput();
            DrawFrame();
            Thread.Sleep(25);

            if (reader.Position >= reader.Length)
            {
                // Some output devices do not immediately report Stopped at EOF,
                // so checking the reader position makes track transitions crisp.
                break;
            }
        }
    }

    private void HandleInput()
    {
        // Process all queued keystrokes so drag/drop text, hotkeys, and repeated
        // arrow presses do not lag behind the visualizer.
        while (Console.KeyAvailable)
        {
            var keyInfo = Console.ReadKey(intercept: true);

            // Give dragged/pasted path text first chance. This prevents file
            // path characters from being interpreted as hotkeys.
            if (HandleBufferedPathInput(keyInfo))
            {
                continue;
            }

            // Single-key playback controls.
            switch (keyInfo.Key)
            {
                case ConsoleKey.Q:
                case ConsoleKey.Escape:
                    quit = true;
                    output?.Stop();
                    break;
                case ConsoleKey.Spacebar:
                    // Toggle pause/resume.
                    if (output?.PlaybackState == PlaybackState.Playing)
                    {
                        output.Pause();
                    }
                    else
                    {
                        output?.Play();
                    }

                    break;
                case ConsoleKey.LeftArrow:
                    // Rewind by five seconds without going before the start.
                    if (reader is not null)
                    {
                        reader.CurrentTime = Max(TimeSpan.Zero, reader.CurrentTime - TimeSpan.FromSeconds(5));
                    }

                    break;
                case ConsoleKey.RightArrow:
                    // Seek forward by five seconds without passing the end.
                    if (reader is not null)
                    {
                        reader.CurrentTime = Min(reader.TotalTime, reader.CurrentTime + TimeSpan.FromSeconds(5));
                    }

                    break;
                case ConsoleKey.N:
                    // Stop this track and advance on the outer loop.
                    skipTrack = true;
                    output?.Stop();
                    break;
                case ConsoleKey.P:
                    // Go back one track. During playback, currentIndex is offset
                    // because the outer loop increments after PlayCurrentTrack.
                    if (queue.Count > 0)
                    {
                        if (output is null)
                        {
                            currentIndex = Math.Clamp(currentIndex - 1, 0, queue.Count - 1);
                        }
                        else
                        {
                            currentIndex = Math.Max(-1, currentIndex - 2);
                            skipTrack = true;
                            output.Stop();
                        }
                    }

                    break;
                case ConsoleKey.A:
                    // Explicit add prompt for users who prefer a clean input
                    // line instead of dragging directly into the visualizer.
                    AddMoreSongs();
                    break;
                case ConsoleKey.UpArrow:
                    // Increase volume and immediately apply it to the current
                    // reader, if one exists.
                    volume = Math.Min(1f, volume + 0.05f);
                    if (reader is not null)
                    {
                        reader.Volume = volume;
                    }

                    break;
                case ConsoleKey.DownArrow:
                    // Decrease volume and immediately apply it to the current
                    // reader, if one exists.
                    volume = Math.Max(0f, volume - 0.05f);
                    if (reader is not null)
                    {
                        reader.Volume = volume;
                    }

                    break;
            }
        }
    }

    private bool HandleBufferedPathInput(ConsoleKeyInfo keyInfo)
    {
        // Enter commits the buffered drag/drop or pasted text as queue items.
        if (keyInfo.Key == ConsoleKey.Enter)
        {
            if (inputBuffer.Length == 0)
            {
                return true;
            }

            AddQueuedText(inputBuffer.ToString());
            inputBuffer.Clear();
            return true;
        }

        // Backspace edits the pending path buffer.
        if (keyInfo.Key == ConsoleKey.Backspace)
        {
            if (inputBuffer.Length > 0)
            {
                inputBuffer.Length--;
                statusMessage = $"Press Enter to add: {PathTools.PreviewDroppedText(inputBuffer.ToString(), 72)}";
            }

            return true;
        }

        // Escape clears a partially typed/dropped path. If there is no buffer,
        // Escape is allowed to continue to the normal quit hotkey.
        if (keyInfo.Key == ConsoleKey.Escape && inputBuffer.Length > 0)
        {
            inputBuffer.Clear();
            statusMessage = "Path entry cleared.";
            return true;
        }

        // Control keys such as arrows are not path text.
        if (char.IsControl(keyInfo.KeyChar))
        {
            return false;
        }

        // If there is already buffered path text, or if the terminal has more
        // characters waiting, treat this as part of dragged/pasted text. For a
        // lone printable key, only buffer it when it is not one of the known
        // single-key commands.
        if (inputBuffer.Length > 0 || Console.KeyAvailable || !IsSingleKeyCommand(keyInfo.Key))
        {
            inputBuffer.Append(keyInfo.KeyChar);
            statusMessage = $"Press Enter to add: {PathTools.PreviewDroppedText(inputBuffer.ToString(), 72)}";
            return true;
        }

        return false;
    }

    private static bool IsSingleKeyCommand(ConsoleKey key)
    {
        // Printable keys that should still act as controls when pressed alone.
        return key is ConsoleKey.Q or ConsoleKey.Spacebar or ConsoleKey.N or ConsoleKey.P or ConsoleKey.A;
    }

    private void AddQueuedText(string text)
    {
        // Parse dragged/pasted terminal text into file paths, filter missing
        // files, and append valid ones to the queue.
        var added = PathTools.ExistingFiles(PathTools.SplitCommandLine(text));
        if (added.Count == 0)
        {
            statusMessage = "No valid files found in dropped text.";
            return;
        }

        queue.AddRange(added);
        statusMessage = $"Queued {added.Count} song{(added.Count == 1 ? string.Empty : "s")}.";
    }

    private void AddMoreSongs()
    {
        // A blocking prompt for adding files. Playback is paused so the prompt
        // does not fight with visualizer drawing.
        var wasPlaying = output?.PlaybackState == PlaybackState.Playing;
        output?.Pause();
        Console.ResetColor();
        Console.CursorVisible = true;
        Console.Clear();
        Console.WriteLine("Drag or paste more music files, then press Enter.");
        Console.Write("> ");

        AddQueuedText(Console.ReadLine() ?? string.Empty);

        Console.CursorVisible = false;
        Console.Clear();
        if (wasPlaying)
        {
            output?.Play();
        }
    }

    private void DrawIdleFrame()
    {
        // Draw a calm, mostly static screen when the queue is finished. This is
        // intentionally not animated like the active visualizer, so it is clear
        // music is stopped.
        var width = Math.Max(52, Console.WindowWidth);
        var height = Math.Max(20, Console.WindowHeight);

        Console.SetCursorPosition(0, 0);
        Console.ForegroundColor = ConsoleColor.Cyan;
        WritePadded(Center("+-=| MUSIC TERMINAL |=-+", width), width);
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        WritePadded(Center("[ STOPPED ]", width), width);
        Console.ForegroundColor = ConsoleColor.Green;
        WritePadded($"QUEUE COMPLETE   {queue.Count} song{(queue.Count == 1 ? string.Empty : "s")} loaded   VOL {(int)(volume * 100),3}%   APP STAYS OPEN", width);
        Console.ForegroundColor = ConsoleColor.DarkGray;
        WritePadded("Drag files anytime, then press Enter   A add prompt   P replay previous   Q quit", width);
        WritePadded(new string('-', width), width);

        var rows = Math.Max(8, height - 8);
        var center = rows / 2;

        // Fill the center area with static instructions.
        for (var row = 0; row < rows; row++)
        {
            Console.ForegroundColor = row == center ? ConsoleColor.DarkGreen : ConsoleColor.DarkGray;
            var chars = new char[width];
            Array.Fill(chars, ' ');

            if (row == center - 1)
            {
                PlaceCentered(chars, "[ stopped ]");
            }
            else if (row == center)
            {
                PlaceCentered(chars, "drop songs here, then press enter to add");
            }
            else if (row == center + 1)
            {
                PlaceCentered(chars, "q / esc exits");
            }

            Console.Write(chars);
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        WritePadded(new string('=', width), width);
        Console.ForegroundColor = ConsoleColor.DarkGreen;
        var prompt = inputBuffer.Length > 0
            ? $"Press Enter to add: {PathTools.PreviewDroppedText(inputBuffer.ToString(), Math.Max(20, width - 22))}"
            : statusMessage;
        WritePadded(Center(prompt, width), width);
        Console.ResetColor();
    }

    private static void PlaceCentered(char[] chars, string text)
    {
        // Writes short text into the middle of a pre-sized console row buffer.
        if (text.Length > chars.Length)
        {
            text = text[..chars.Length];
        }

        var start = Math.Max(0, (chars.Length - text.Length) / 2);
        for (var i = 0; i < text.Length && start + i < chars.Length; i++)
        {
            chars[start + i] = text[i];
        }
    }

    private void DrawFrame()
    {
        // Do nothing if a frame is requested outside active playback setup.
        if (reader is null || analyzer is null || output is null)
        {
            return;
        }

        // Console layout. The frame is redrawn from the top-left every tick,
        // with a minimum size so the visualizer remains readable.
        var width = Math.Max(52, Console.WindowWidth);
        var height = Math.Max(20, Console.WindowHeight);
        var barCount = Math.Clamp(width - 4, 28, 132);
        var spectrum = analyzer.GetSpectrum(barCount);
        var levels = analyzer.GetLevels();
        var left = Math.Max(0, (width - barCount) / 2);
        var footerRows = 5;
        var barHeight = Math.Max(8, height - footerRows - 4);

        // Beat is a quick-and-simple transient estimate: current peak minus the
        // smoothed level. Big jumps become visual pulses.
        var beat = Math.Clamp((levels.Peak - levels.Smoothed) * 2.8f, 0f, 1f);
        var isPlaying = output.PlaybackState == PlaybackState.Playing;

        // When paused, freeze decorative motion so the visualizer does not look
        // like it is still responding to music.
        var phase = isPlaying ? frameClock.Elapsed.TotalSeconds : 0;

        Console.SetCursorPosition(0, 0);
        Console.ForegroundColor = ConsoleColor.Cyan;
        WritePadded(Center("+-=| MUSIC TERMINAL |=-+", width), width);
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        WritePadded(Center($"+=[ {Path.GetFileName(queue[currentIndex])} ]=+", width), width);

        Console.ForegroundColor = beat > 0.45f ? ConsoleColor.White : ConsoleColor.Green;
        var state = isPlaying ? "PLAY" : "PAUSE";
        var progress = reader.TotalTime.TotalSeconds <= 0
            ? 0
            : reader.CurrentTime.TotalSeconds / reader.TotalTime.TotalSeconds;
        WritePadded($"{state}  {currentIndex + 1}/{queue.Count}  {FormatTime(reader.CurrentTime)} / {FormatTime(reader.TotalTime)}  VOL {(int)(volume * 100),3}%  {ProgressBar(progress, Math.Max(10, width - 61))}", width);

        Console.ForegroundColor = ConsoleColor.DarkGray;
        WritePadded("SPACE pause   LEFT/RIGHT seek   N next   P previous   Drag files + Enter to queue   Q quit", width);
        WritePadded(new string('-', width), width);

        for (var row = 0; row < barHeight; row++)
        {
            Console.ForegroundColor = ColorForRow(row, barHeight, beat);

            // Each row has a threshold from top to bottom. A bar appears in this
            // row when the spectrum value reaches that threshold.
            var threshold = 1.0 - row / (double)barHeight;
            var line = new char[width];
            Array.Fill(line, ' ');

            for (var i = 0; i < barCount; i++)
            {
                // Add reactive movement only while playing. Spectrum data gives
                // the real audio shape; pulse/scan add retro visual energy.
                var pulse = isPlaying ? 0.08 * beat * Math.Sin(phase * 12.0 + i * 0.31) : 0;
                var scan = isPlaying ? 0.04 * Math.Sin(phase * 3.0 - row * 0.45 + i * 0.09) : 0;
                var value = Math.Clamp(spectrum[i] + pulse + scan, 0, 1);
                line[left + i] = value >= threshold ? GlyphFor(value, threshold, beat) : ' ';
            }

            Console.Write(line);
        }

        Console.ForegroundColor = ConsoleColor.Magenta;
        WritePadded(BuildWaveLine(width, levels.Rms, levels.Peak, phase, isPlaying ? beat : 0), width);

        Console.ForegroundColor = ConsoleColor.Cyan;
        WritePadded(new string('=', width), width);
        Console.ForegroundColor = ConsoleColor.DarkGreen;
        var footer = inputBuffer.Length > 0
            ? $"Press Enter to add: {PathTools.PreviewDroppedText(inputBuffer.ToString(), Math.Max(20, width - 22))}"
            : QueuePreview(width);
        WritePadded(Center(footer, width), width);
        Console.ResetColor();
    }

    private string QueuePreview(int width)
    {
        // Shows several upcoming tracks in the footer and adds a "+N more"
        // suffix when the queue is longer than the visible preview.
        if (currentIndex + 1 >= queue.Count)
        {
            return "UP NEXT: empty - drag songs, then press Enter";
        }

        var upcoming = queue
            .Skip(currentIndex + 1)
            .Take(4)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();

        var remaining = queue.Count - currentIndex - 1 - upcoming.Count;
        var text = $"UP NEXT: {string.Join("  |  ", upcoming)}";
        if (remaining > 0)
        {
            text += $"  |  +{remaining} more";
        }

        return text.Length > width ? text[..width] : text;
    }

    private static string BuildWaveLine(int width, float rms, float peak, double phase, float beat)
    {
        // Builds the animated lower waveform strip. It is decorative but driven
        // by the current RMS/peak levels, so louder sections look denser.
        var chars = new char[width];
        var amp = Math.Clamp(rms * 5f + peak * 1.8f + beat * 0.5f, 0.12f, 1f);
        const string glyphs = "._-~=+*#%@";

        for (var i = 0; i < width; i++)
        {
            var wave = Math.Sin(phase * 8.0 + i * 0.36) + Math.Sin(phase * 3.5 - i * 0.13) * 0.55;
            var value = Math.Clamp(Math.Abs(wave) * amp / 1.55, 0, 1);
            chars[i] = glyphs[(int)Math.Round(value * (glyphs.Length - 1))];
        }

        return new string(chars);
    }

    private static char GlyphFor(double value, double threshold, float beat)
    {
        // Chooses a character based on how far above the row threshold the
        // current spectrum value is. Stronger bins get heavier glyphs.
        var delta = value - threshold + beat * 0.08;
        if (delta > 0.34)
        {
            return '@';
        }

        if (delta > 0.23)
        {
            return '#';
        }

        if (delta > 0.13)
        {
            return '*';
        }

        if (delta > 0.06)
        {
            return '+';
        }

        return '.';
    }

    private static ConsoleColor ColorForRow(int row, int rows, float beat)
    {
        // Top rows are red/yellow to feel like a VU meter; lower rows are green.
        // A strong beat can flash the top rows white.
        if (beat > 0.65f && row < rows / 4)
        {
            return ConsoleColor.White;
        }

        var third = rows / 3;
        if (row < third)
        {
            return ConsoleColor.Red;
        }

        if (row < third * 2)
        {
            return ConsoleColor.Yellow;
        }

        return ConsoleColor.Green;
    }

    private static string Center(string text, int width)
    {
        // Returns text padded on the left so it appears centered in a row.
        if (text.Length >= width)
        {
            return text[..width];
        }

        var padding = (width - text.Length) / 2;
        return new string(' ', padding) + text;
    }

    private static string ProgressBar(double progress, int width)
    {
        // Renders the song progress bar with fixed-width ASCII characters.
        progress = Math.Clamp(progress, 0, 1);
        var filled = (int)Math.Round(progress * width);
        return "[" + new string('=', filled) + new string('-', width - filled) + "]";
    }

    private static string FormatTime(TimeSpan value)
    {
        // Displays h:mm:ss for long tracks and m:ss for shorter ones.
        return value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : value.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    private static void WritePadded(string value, int width)
    {
        // Writes a row and pads/truncates it so old frame content is overwritten.
        if (value.Length > width)
        {
            value = value[..width];
        }

        Console.Write(value.PadRight(width));
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left <= right ? left : right;

    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left >= right ? left : right;

    private void DisposeCurrentTrack()
    {
        // Release active audio resources before opening another track or
        // entering the stopped screen.
        output?.Dispose();
        reader?.Dispose();
        output = null;
        reader = null;
        analyzer = null;
    }

    public void Dispose()
    {
        // Final cleanup for using/Dispose. Restores terminal color and cursor.
        if (disposed)
        {
            return;
        }

        DisposeCurrentTrack();
        Console.ResetColor();
        Console.CursorVisible = true;
        disposed = true;
    }
}

sealed class MacTerminalMusicPlayer : IDisposable
{
    // macOS queue. Playback is handled by launching `afplay` for each path.
    private readonly List<string> queue;

    // Clock used to animate the simulated macOS visualizer.
    private readonly Stopwatch frameClock = Stopwatch.StartNew();

    // Drag/drop path buffer, same idea as the Windows player.
    private readonly StringBuilder inputBuffer = new();

    // The currently running `afplay` process.
    private Process? process;

    // Queue and loop state.
    private int currentIndex;
    private bool quit;
    private bool skipTrack;
    private bool disposed;

    // Footer message shown when the drop buffer is empty.
    private string statusMessage = "Drag files anytime, then press Enter to queue them.";

    public MacTerminalMusicPlayer(List<string> paths)
    {
        queue = paths;
    }

    public void Run()
    {
        // Prepare the terminal for repeated full-screen redraws.
        Console.OutputEncoding = Encoding.UTF8;
        Console.CursorVisible = false;
        Console.Clear();

        // Same lifetime model as the Windows player: keep the app open until
        // the user quits, even when the queue is empty or finished.
        while (!quit)
        {
            if (currentIndex < queue.Count)
            {
                PlayCurrentTrack();
                if (!quit && skipTrack)
                {
                    skipTrack = false;
                    currentIndex++;
                }
                else if (!quit)
                {
                    currentIndex++;
                }
            }
            else
            {
                StopCurrentTrack();
                DrawIdleFrame();
                HandleInput();
                Thread.Sleep(50);
            }
        }
    }

    private void PlayCurrentTrack()
    {
        // Start a fresh afplay process for this track. Because afplay owns the
        // actual audio, this macOS visualizer is animated rather than FFT-driven.
        StopCurrentTrack();
        var startedAt = Stopwatch.StartNew();
        process = StartAfplay(queue[currentIndex]);

        // Keep drawing while afplay is alive. When afplay exits, the song ended.
        while (!quit && !skipTrack && process is { HasExited: false })
        {
            HandleInput();
            DrawFrame(startedAt.Elapsed);
            Thread.Sleep(33);
        }
    }

    private static Process StartAfplay(string path)
    {
        // `afplay` is Apple's built-in command-line audio player. ArgumentList
        // avoids shell quoting issues for paths with spaces.
        var startInfo = new ProcessStartInfo("afplay")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(path);
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start macOS afplay.");
    }

    private void HandleInput()
    {
        // Handles queued keystrokes, with dragged/pasted file paths taking
        // priority over single-key commands.
        while (Console.KeyAvailable)
        {
            var keyInfo = Console.ReadKey(intercept: true);
            if (HandleBufferedPathInput(keyInfo))
            {
                continue;
            }

            switch (keyInfo.Key)
            {
                case ConsoleKey.Q:
                case ConsoleKey.Escape:
                    quit = true;
                    StopCurrentTrack();
                    break;
                case ConsoleKey.N:
                    skipTrack = true;
                    StopCurrentTrack();
                    break;
                case ConsoleKey.P:
                    if (queue.Count > 0)
                    {
                        if (process is null)
                        {
                            currentIndex = Math.Clamp(currentIndex - 1, 0, queue.Count - 1);
                        }
                        else
                        {
                            currentIndex = Math.Max(-1, currentIndex - 2);
                            skipTrack = true;
                            StopCurrentTrack();
                        }
                    }

                    break;
                case ConsoleKey.A:
                    AddMoreSongs();
                    break;
            }
        }
    }

    private bool HandleBufferedPathInput(ConsoleKeyInfo keyInfo)
    {
        // Enter commits buffered drag/drop text into the queue.
        if (keyInfo.Key == ConsoleKey.Enter)
        {
            if (inputBuffer.Length == 0)
            {
                return true;
            }

            AddQueuedText(inputBuffer.ToString());
            inputBuffer.Clear();
            return true;
        }

        // Let users correct a partially dropped or pasted path.
        if (keyInfo.Key == ConsoleKey.Backspace)
        {
            if (inputBuffer.Length > 0)
            {
                inputBuffer.Length--;
                statusMessage = $"Press Enter to add: {PathTools.PreviewDroppedText(inputBuffer.ToString(), 72)}";
            }

            return true;
        }

        // Escape clears pending path text before it is treated as quit.
        if (keyInfo.Key == ConsoleKey.Escape && inputBuffer.Length > 0)
        {
            inputBuffer.Clear();
            statusMessage = "Path entry cleared.";
            return true;
        }

        // Non-printing keys belong to normal command handling.
        if (char.IsControl(keyInfo.KeyChar))
        {
            return false;
        }

        // Buffer printable dragged/pasted characters, but keep known single-key
        // commands working when pressed by themselves.
        if (inputBuffer.Length > 0 || Console.KeyAvailable || !IsSingleKeyCommand(keyInfo.Key))
        {
            inputBuffer.Append(keyInfo.KeyChar);
            statusMessage = $"Press Enter to add: {PathTools.PreviewDroppedText(inputBuffer.ToString(), 72)}";
            return true;
        }

        return false;
    }

    private static bool IsSingleKeyCommand(ConsoleKey key)
    {
        // macOS backend supports only commands that afplay can reasonably do.
        // Seeking and volume are not implemented because afplay is external.
        return key is ConsoleKey.Q or ConsoleKey.N or ConsoleKey.P or ConsoleKey.A;
    }

    private void AddQueuedText(string text)
    {
        // Parse terminal text into valid files and append them to the queue.
        var added = PathTools.ExistingFiles(PathTools.SplitCommandLine(text));
        if (added.Count == 0)
        {
            statusMessage = "No valid files found in dropped text.";
            return;
        }

        queue.AddRange(added);
        statusMessage = $"Queued {added.Count} song{(added.Count == 1 ? string.Empty : "s")}.";
    }

    private void AddMoreSongs()
    {
        // Blocking add prompt for a clean drag/drop line.
        Console.ResetColor();
        Console.CursorVisible = true;
        Console.Clear();
        Console.WriteLine("Drag or paste more music files, then press Enter.");
        Console.Write("> ");

        AddQueuedText(Console.ReadLine() ?? string.Empty);

        Console.CursorVisible = false;
        Console.Clear();
    }

    private void DrawFrame(TimeSpan elapsed)
    {
        // macOS cannot read samples from afplay, so this draws a lively faux
        // spectrum while afplay is running. It still respects queue state and
        // stopped state, but it is not true FFT audio analysis.
        var width = Math.Max(52, Console.WindowWidth);
        var height = Math.Max(20, Console.WindowHeight);
        var barCount = Math.Clamp(width - 4, 28, 132);
        var barHeight = Math.Max(8, height - 9);
        var left = Math.Max(0, (width - barCount) / 2);
        var phase = frameClock.Elapsed.TotalSeconds;
        var seed = Math.Abs(queue[currentIndex].GetHashCode());

        Console.SetCursorPosition(0, 0);
        Console.ForegroundColor = ConsoleColor.Cyan;
        WritePadded(Center("+-=| MUSIC TERMINAL |=-+", width), width);
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        WritePadded(Center($"+=[ {Path.GetFileName(queue[currentIndex])} ]=+", width), width);
        Console.ForegroundColor = ConsoleColor.Green;
        WritePadded($"PLAY  {currentIndex + 1}/{queue.Count}  {FormatTime(elapsed)}  macOS afplay backend", width);
        Console.ForegroundColor = ConsoleColor.DarkGray;
        WritePadded("N next   P previous   A add songs   Drag files + Enter to queue   Q quit", width);
        WritePadded(new string('-', width), width);

        for (var row = 0; row < barHeight; row++)
        {
            Console.ForegroundColor = ColorForRow(row, barHeight, 0.35f);
            var threshold = 1.0 - row / (double)barHeight;
            var line = new char[width];
            Array.Fill(line, ' ');

            for (var i = 0; i < barCount; i++)
            {
                // Create an animated shape from sine waves. The file path hash
                // nudges each track into a slightly different pattern.
                var band = i / (double)Math.Max(1, barCount - 1);
                var shape = Math.Sin(phase * (3.0 + band * 9.0) + i * 0.17 + seed * 0.001);
                var pulse = Math.Sin(phase * 7.0 + i * 0.09) * 0.18;
                var value = Math.Clamp(0.42 + shape * 0.32 + pulse + (1 - band) * 0.12, 0, 1);
                line[left + i] = value >= threshold ? GlyphFor(value, threshold, 0.25f) : ' ';
            }

            Console.Write(line);
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        WritePadded(new string('=', width), width);
        Console.ForegroundColor = ConsoleColor.DarkGreen;
        var footer = inputBuffer.Length > 0
            ? $"Press Enter to add: {PathTools.PreviewDroppedText(inputBuffer.ToString(), Math.Max(20, width - 22))}"
            : QueuePreview(width);
        WritePadded(Center(footer, width), width);
        Console.ResetColor();
    }

    private void DrawIdleFrame()
    {
        // Static stopped screen for macOS, matching the Windows stopped screen.
        var width = Math.Max(52, Console.WindowWidth);
        var height = Math.Max(20, Console.WindowHeight);

        Console.SetCursorPosition(0, 0);
        Console.ForegroundColor = ConsoleColor.Cyan;
        WritePadded(Center("+-=| MUSIC TERMINAL |=-+", width), width);
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        WritePadded(Center("[ STOPPED ]", width), width);
        Console.ForegroundColor = ConsoleColor.Green;
        WritePadded($"QUEUE COMPLETE   {queue.Count} song{(queue.Count == 1 ? string.Empty : "s")} loaded   APP STAYS OPEN", width);
        Console.ForegroundColor = ConsoleColor.DarkGray;
        WritePadded("Drag files anytime, then press Enter   A add prompt   P replay previous   Q quit", width);
        WritePadded(new string('-', width), width);

        var rows = Math.Max(8, height - 8);
        var center = rows / 2;
        for (var row = 0; row < rows; row++)
        {
            Console.ForegroundColor = row == center ? ConsoleColor.DarkGreen : ConsoleColor.DarkGray;
            var chars = new char[width];
            Array.Fill(chars, ' ');

            if (row == center - 1)
            {
                PlaceCentered(chars, "[ stopped ]");
            }
            else if (row == center)
            {
                PlaceCentered(chars, "drop songs here, then press enter to add");
            }
            else if (row == center + 1)
            {
                PlaceCentered(chars, "q / esc exits");
            }

            Console.Write(chars);
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        WritePadded(new string('=', width), width);
        Console.ForegroundColor = ConsoleColor.DarkGreen;
        var prompt = inputBuffer.Length > 0
            ? $"Press Enter to add: {PathTools.PreviewDroppedText(inputBuffer.ToString(), Math.Max(20, width - 22))}"
            : statusMessage;
        WritePadded(Center(prompt, width), width);
        Console.ResetColor();
    }

    private string QueuePreview(int width)
    {
        // Footer preview for upcoming tracks.
        if (currentIndex + 1 >= queue.Count)
        {
            return "UP NEXT: empty - drag songs, then press Enter";
        }

        var upcoming = queue
            .Skip(currentIndex + 1)
            .Take(4)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();

        var remaining = queue.Count - currentIndex - 1 - upcoming.Count;
        var text = $"UP NEXT: {string.Join("  |  ", upcoming)}";
        if (remaining > 0)
        {
            text += $"  |  +{remaining} more";
        }

        return text.Length > width ? text[..width] : text;
    }

    private static string Center(string text, int width)
    {
        // Centers text inside the current console width.
        if (text.Length >= width)
        {
            return text[..width];
        }

        var padding = (width - text.Length) / 2;
        return new string(' ', padding) + text;
    }

    private static void WritePadded(string value, int width)
    {
        // Pads/truncates rows so previous frame content is erased.
        if (value.Length > width)
        {
            value = value[..width];
        }

        Console.Write(value.PadRight(width));
    }

    private static void PlaceCentered(char[] chars, string text)
    {
        // Places short status text inside a blank char row.
        if (text.Length > chars.Length)
        {
            text = text[..chars.Length];
        }

        var start = Math.Max(0, (chars.Length - text.Length) / 2);
        for (var i = 0; i < text.Length && start + i < chars.Length; i++)
        {
            chars[start + i] = text[i];
        }
    }

    private static ConsoleColor ColorForRow(int row, int rows, float beat)
    {
        // Reuses the VU-style color mapping from the Windows renderer.
        if (beat > 0.65f && row < rows / 4)
        {
            return ConsoleColor.White;
        }

        var third = rows / 3;
        if (row < third)
        {
            return ConsoleColor.Red;
        }

        if (row < third * 2)
        {
            return ConsoleColor.Yellow;
        }

        return ConsoleColor.Green;
    }

    private static char GlyphFor(double value, double threshold, float beat)
    {
        // Converts a normalized bar value into a retro ASCII glyph.
        var delta = value - threshold + beat * 0.08;
        if (delta > 0.34)
        {
            return '@';
        }

        if (delta > 0.23)
        {
            return '#';
        }

        if (delta > 0.13)
        {
            return '*';
        }

        if (delta > 0.06)
        {
            return '+';
        }

        return '.';
    }

    private static string FormatTime(TimeSpan value)
    {
        // Formats elapsed afplay time for the header.
        return value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : value.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    private void StopCurrentTrack()
    {
        // Stop afplay when skipping, quitting, or moving to idle.
        if (process is { HasExited: false })
        {
            process.Kill(entireProcessTree: true);
        }

        process?.Dispose();
        process = null;
    }

    public void Dispose()
    {
        // Final cleanup for the external process and terminal state.
        if (disposed)
        {
            return;
        }

        StopCurrentTrack();
        Console.ResetColor();
        Console.CursorVisible = true;
        disposed = true;
    }
}

sealed class AnalyzingSampleProvider : ISampleProvider
{
    // FFT size. Must be a power of two for NAudio's FFT implementation.
    private const int FftLength = 2048;

    // Wrapped audio source. Reads from this provider are forwarded after
    // samples are copied into analysis buffers.
    private readonly ISampleProvider source;

    // Number of source channels. Samples are mixed to mono for analysis.
    private readonly int channels;

    // Buffer used by the FFT. Complex values hold real/imaginary parts.
    private readonly Complex[] fftBuffer = new Complex[FftLength];

    // Latest smoothed FFT magnitudes.
    private readonly float[] magnitudes = new float[FftLength / 2];

    // Per-bin adaptive peak values. This prevents bass from permanently
    // dominating the display and helps higher frequencies show movement.
    private readonly float[] adaptivePeaks = new float[180];

    // Recent mono samples used to calculate RMS level.
    private readonly float[] recentSamples = new float[256];

    // Synchronizes audio-thread writes with UI-thread reads.
    private readonly object sync = new();

    // Ring-buffer positions and current level measurements.
    private int fftPosition;
    private int samplePosition;
    private float rms;
    private float peak;
    private float smoothed;

    public AnalyzingSampleProvider(ISampleProvider source, int channels)
    {
        // Preserve the wrapped provider's format because the output device sees
        // this analyzer as if it were the original audio stream.
        this.source = source;
        this.channels = Math.Max(1, channels);
        WaveFormat = source.WaveFormat;
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        // NAudio calls Read to request audio for playback. We pass through the
        // original samples unchanged, while also copying a mono mix into the
        // analyzer.
        var samplesRead = source.Read(buffer, offset, count);
        for (var i = 0; i < samplesRead; i += channels)
        {
            var mixed = 0f;
            var availableChannels = Math.Min(channels, samplesRead - i);
            for (var ch = 0; ch < availableChannels; ch++)
            {
                // Average all available channels into one mono analysis sample.
                mixed += buffer[offset + i + ch];
            }

            AddSample(mixed / availableChannels);
        }

        return samplesRead;
    }

    public float[] GetSpectrum(int bins)
    {
        // Converts raw FFT magnitudes into the number of bars requested by the
        // terminal width.
        bins = Math.Clamp(bins, 8, 180);
        var values = new float[bins];

        lock (sync)
        {
            for (var bin = 0; bin < bins; bin++)
            {
                // Nonlinear bin mapping gives more screen space to low/mid
                // frequencies, which are usually more musically useful.
                var start = (int)(Math.Pow(bin / (double)bins, 1.9) * (magnitudes.Length - 1));
                var end = (int)(Math.Pow((bin + 1) / (double)bins, 1.9) * (magnitudes.Length - 1));
                end = Math.Max(start + 1, end);

                var peakMagnitude = 0f;
                for (var i = start; i < end && i < magnitudes.Length; i++)
                {
                    // Use peak magnitude inside this frequency range.
                    peakMagnitude = Math.Max(peakMagnitude, magnitudes[i]);
                }

                // Each visual bin tracks its own recent peak. This makes quiet
                // treble bands visible even when bass is strong.
                adaptivePeaks[bin] = Math.Max(0.015f, Math.Max(adaptivePeaks[bin] * 0.992f, peakMagnitude));
                var normalized = peakMagnitude / adaptivePeaks[bin];

                // Slight high-frequency lift so the right side of the visualizer
                // does not look dead on bass-heavy songs.
                normalized *= 0.9f + (bin / (float)Math.Max(1, bins - 1)) * 0.45f;

                // Gamma curve exaggerates smaller movement into visible bars.
                values[bin] = MathF.Min(1f, MathF.Pow(normalized, 0.42f));
            }
        }

        return values;
    }

    public AudioLevels GetLevels()
    {
        // Returns the latest volume measurements for beat/pulse rendering.
        lock (sync)
        {
            return new AudioLevels(rms, peak, smoothed);
        }
    }

    private void AddSample(float value)
    {
        // Adds one mono sample to all analysis buffers.
        lock (sync)
        {
            // Ring buffer for short-term RMS.
            recentSamples[samplePosition] = value;
            samplePosition = (samplePosition + 1) % recentSamples.Length;

            var abs = Math.Abs(value);

            // Peak falls quickly, smoothed level falls slowly. Their difference
            // is used as a lightweight beat/transient estimate.
            peak = Math.Max(peak * 0.94f, abs);
            smoothed = smoothed * 0.985f + abs * 0.015f;

            var total = 0f;
            for (var i = 0; i < recentSamples.Length; i++)
            {
                // RMS is sqrt(mean(sample^2)).
                total += recentSamples[i] * recentSamples[i];
            }

            rms = MathF.Sqrt(total / recentSamples.Length);

            // Window the sample before FFT to reduce spectral leakage.
            fftBuffer[fftPosition].X = (float)(value * FastFourierTransform.HammingWindow(fftPosition, FftLength));
            fftBuffer[fftPosition].Y = 0;
            fftPosition++;

            if (fftPosition < FftLength)
            {
                // Wait until the FFT buffer is full.
                return;
            }

            // Run the FFT and smooth magnitudes so bars do not flicker harshly.
            FastFourierTransform.FFT(true, (int)Math.Log2(FftLength), fftBuffer);
            for (var i = 0; i < magnitudes.Length; i++)
            {
                var magnitude = MathF.Sqrt(fftBuffer[i].X * fftBuffer[i].X + fftBuffer[i].Y * fftBuffer[i].Y);
                magnitudes[i] = magnitudes[i] * 0.62f + magnitude * 0.38f;
            }

            fftPosition = 0;
        }
    }
}

// Small immutable value object used to move level data from the analyzer to
// the renderer.
readonly record struct AudioLevels(float Rms, float Peak, float Smoothed);

static class PathTools
{
    public static List<string> GetAudioPaths(string[] args)
    {
        // Startup path collection. If command-line args are present, treat them
        // as file paths. Otherwise prompt the user to drag/paste paths.
        if (args.Length > 0)
        {
            return ExistingFiles(args);
        }

        Console.WriteLine("musicviz - retro terminal music player");
        Console.WriteLine();
        Console.WriteLine("Drag one or more music files into this window, or paste paths, then press Enter.");
        Console.WriteLine("Supported formats depend on Windows codecs. MP3, WAV, and M4A usually work.");
        Console.WriteLine();
        Console.Write("> ");

        return ExistingFiles(SplitCommandLine(Console.ReadLine() ?? string.Empty));
    }

    public static List<string> ExistingFiles(IEnumerable<string> paths)
    {
        // Filters input strings down to existing files and normalizes them to
        // absolute paths. Missing files are reported but do not stop the app.
        var files = new List<string>();
        foreach (var path in paths.Select(p => p.Trim().Trim('"')).Where(p => p.Length > 0))
        {
            if (File.Exists(path))
            {
                files.Add(Path.GetFullPath(path));
            }
            else
            {
                Console.Error.WriteLine($"Skipping missing file: {path}");
            }
        }

        return files;
    }

    public static List<string> SplitCommandLine(string text)
    {
        // Splits terminal-dropped text into paths. Windows Terminal and macOS
        // Terminal usually quote paths with spaces, so this parser respects
        // quotes instead of splitting blindly on every space.
        var values = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var ch in text)
        {
            if (ch == '"')
            {
                // Toggle quoted mode; quote characters themselves are not kept.
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                // Outside quotes, whitespace separates paths.
                AddCurrent();
                continue;
            }

            current.Append(ch);
        }

        AddCurrent();
        return values;

        void AddCurrent()
        {
            // Adds the current token, if any, and resets the builder.
            if (current.Length == 0)
            {
                return;
            }

            values.Add(current.ToString());
            current.Clear();
        }
    }

    public static string PreviewDroppedText(string text, int maxLength)
    {
        // Builds a short footer preview from dragged/pasted text, showing only
        // filenames so long full paths do not take over the terminal.
        text = string.Join(' ', SplitCommandLine(text).Select(Path.GetFileName).Where(name => !string.IsNullOrWhiteSpace(name)));
        if (string.IsNullOrWhiteSpace(text))
        {
            text = "dropped file";
        }

        if (text.Length <= maxLength)
        {
            return text;
        }

        return text[..Math.Max(0, maxLength - 3)] + "...";
    }
}
