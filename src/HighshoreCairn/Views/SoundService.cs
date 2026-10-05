using System.Media;
using HighshoreCairn.Services;
using HighshoreCairn.ViewModels;

namespace HighshoreCairn.Views;

/// <summary>
/// Plays the notification sounds of the tomato timer. The built-in sounds are generated in memory
/// (see <see cref="ToneSynth"/>); "Custom" plays a .wav file chosen by the user.
/// </summary>
public class SoundService : ISoundService
{
    private SoundPlayer? _player;
    private int _counter;

    public void Play(string sound, int volume, string? customFile = null)
    {
        try
        {
            if (volume <= 0) return;
            string? file;
            if (sound == "Custom")
            {
                if (string.IsNullOrEmpty(customFile) || !File.Exists(customFile)) return;
                // At full volume the file is played as it is; otherwise a quieter copy is played.
                file = volume >= 100 ? customFile : WriteTemp(ToneSynth.ScaleWav(File.ReadAllBytes(customFile), volume));
            }
            else
            {
                var wav = ToneSynth.Render(sound, volume);
                file = wav is null ? null : WriteTemp(wav);
            }
            if (file is null) return;

            // The sound is played from a file, in the background: Windows reads the file by itself, so
            // nothing in the memory of the app has to stay in place while it plays.
            _player?.Stop();
            _player?.Dispose();
            _player = new SoundPlayer(file);
            _player.Play();
        }
        catch
        {
            // No sound device, unsupported file...: the timer keeps working silently.
        }
    }

    /// <summary>A few rotating temporary files: the previous sound may still be playing.</summary>
    private string WriteTemp(byte[] wav)
    {
        var dir = Path.Combine(Path.GetTempPath(), "Cairn");
        Directory.CreateDirectory(dir);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var file = Path.Combine(dir, $"timer-sound-{_counter++ % 4}.wav");
            try
            {
                File.WriteAllBytes(file, wav);
                return file;
            }
            catch (IOException)
            {
                // still in use by the previous sound: try the next one
            }
        }
        throw new IOException("No temporary file available for the sound.");
    }
}
