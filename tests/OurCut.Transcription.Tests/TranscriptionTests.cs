using System.Diagnostics;
using OurCut.Core.Transcripts;
using OurCut.Transcription.Models;

namespace OurCut.Transcription.Tests;

public class WordBuilderTests
{
    [Fact]
    public void Tokens_become_words_with_punctuation_attached()
    {
        string[] tokens = [" A", "sk", " not", ",", " co", "un", "try", "."];
        float[] starts = [0.0f, 0.24f, 0.40f, 0.60f, 0.96f, 1.04f, 1.12f, 1.60f];

        var words = WordBuilder.Build(tokens, starts, offset: 10, end: 12);

        Assert.Equal(["Ask", "not,", "country."], words.Select(w => w.Text));
        Assert.Equal([10.0, 10.4, 10.96], words.Select(w => Math.Round(w.Start, 2)));
        Assert.Equal(10.4, words[0].End, 2);
        // The last word ends a little after its last token, within the piece.
        Assert.Equal(11.9, words[2].End, 2);
    }

    [Fact]
    public void A_word_before_a_long_pause_does_not_stretch_over_it()
    {
        var words = WordBuilder.Build(["▁um", "▁so"], [0f, 5f], 0, 6);
        Assert.Equal("um", words[0].Text);
        Assert.InRange(words[0].End, 0.2, 0.5);
    }
}

public class EstimatedTimesTests
{
    [Fact]
    public void Words_without_times_are_spread_over_the_speech()
    {
        // 1 s silence, 2 s of tone, 1 s silence, 1 s of tone.
        var samples = Enumerable.Range(0, 16000 * 5).Select(i =>
        {
            double t = i / 16000.0;
            return t is >= 1 and < 3 or >= 4 ? (float)(0.3 * Math.Sin(2 * Math.PI * 200 * t)) : 0f;
        }).ToArray();

        var words = WordBuilder.Estimate(["one", "two", "three"], samples, offset: 100);

        Assert.Equal(["one", "two", "three"], words.Select(w => w.Text));
        Assert.Equal(101, words[0].Start, 1);
        Assert.Equal(105, words[2].End, 1);
        // The gap between the stretches of speech is skipped, not given to a word.
        Assert.True(words[2].Start >= 102.2);
    }
}

public class TranscriptTests
{
    [Fact]
    public void Phrases_end_at_sentences_and_long_pauses()
    {
        var t = new Transcript("m", "auto",
        [
            new("Hello", 0, 0.4), new("there.", 0.5, 0.9), new("So", 1.0, 1.2), new("um", 1.3, 1.5),
            new("next", 3.0, 3.3), new("part", 3.4, 3.8),
        ]);

        var phrases = t.Phrases();

        Assert.Equal(["Hello there.", "So um", "next part"], phrases.Select(p => p.Text));
        Assert.Equal((1.0, 1.5, 2, 2), (phrases[1].Start, phrases[1].End, phrases[1].FirstWord, phrases[1].WordCount));
        Assert.Equal(["um", "next"], t.Between(1.4, 3.1).Select(w => w.Text));
    }
}

public class TranscriptionPipelineTests
{
    /// <summary>Records the pieces it is given and says one word per piece.</summary>
    private sealed class FakeRecognizer : ISpeechRecognizer
    {
        public List<(double Offset, double Length)> Pieces { get; } = [];

        public IReadOnlyList<Word> Recognize(float[] samples, double offset)
        {
            Pieces.Add((offset, (double)samples.Length / TranscriptionPipeline.SampleRate));
            return [new Word($"piece{Pieces.Count}", offset, offset + 0.5)];
        }

        public void Dispose()
        {
        }
    }

    /// <summary>70 s of tone with silent gaps at 21–21.5 s and 44–44.5 s.</summary>
    private static MemoryStream Audio()
    {
        const int Rate = TranscriptionPipeline.SampleRate;
        var ms = new MemoryStream();
        var bytes = new byte[4];
        for (int i = 0; i < 70 * Rate; i++)
        {
            double t = (double)i / Rate;
            bool silent = t is >= 21 and < 21.5 or >= 44 and < 44.5;
            float v = silent ? 0 : (float)(0.3 * Math.Sin(2 * Math.PI * 220 * t));
            BitConverter.TryWriteBytes(bytes, v);
            ms.Write(bytes);
        }
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public async Task Long_audio_is_cut_into_pieces_at_its_pauses()
    {
        var recognizer = new FakeRecognizer();
        var progress = new List<double>();

        var words = await TranscriptionPipeline.RunAsync(Audio(), 70, recognizer, (_, p) => progress.Add(p), TestContext.Current.CancellationToken);

        // 70 s in pieces of at most 28 s: cut in the gaps at 21 s and 44 s, then the rest.
        Assert.Equal(3, recognizer.Pieces.Count);
        Assert.All(recognizer.Pieces, p => Assert.True(p.Length <= TranscriptionPipeline.MaxPiece));
        Assert.InRange(recognizer.Pieces[0].Length, 21, 21.5);
        Assert.InRange(recognizer.Pieces[1].Offset + recognizer.Pieces[1].Length, 44, 44.5);
        for (int i = 1; i < recognizer.Pieces.Count; i++)
            Assert.Equal(recognizer.Pieces[i - 1].Offset + recognizer.Pieces[i - 1].Length, recognizer.Pieces[i].Offset, 6);
        Assert.Equal(70, recognizer.Pieces[^1].Offset + recognizer.Pieces[^1].Length, 3);
        Assert.Equal(["piece1", "piece2", "piece3"], words.Select(w => w.Text));
        Assert.Equal(1, progress[^1], 3);
    }

    [Fact]
    public void The_cut_goes_to_the_quietest_moment()
    {
        var samples = Enumerable.Range(0, 16000 * 3).Select(i => i is > 30000 and < 33000 ? 0f : 0.5f).ToArray();
        Assert.InRange(TranscriptionPipeline.QuietestCut(samples, 16000, 48000), 30000, 33000);
    }
}

/// <summary>
/// Recognizes real speech with an installed model. Runs when OURCUT_MODELS_DIR has the model (see
/// RealDownloadTests); skipped otherwise.
/// </summary>
public class RealRecognitionTests
{
    private static string? ModelDirectory(TranscriptionModel model) =>
        Environment.GetEnvironmentVariable("OURCUT_MODELS_DIR") is { Length: > 0 } dir && new ModelStore(dir).IsInstalled(model)
            ? new ModelStore(dir).DirectoryOf(model)
            : null;

    /// <summary>16 kHz mono float samples of a file, via ffmpeg.</summary>
    private static async Task<byte[]> Pcm(string path, double padSeconds = 0)
    {
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardOutput = true };
        foreach (string a in (string[])["-v", "error", "-i", path, "-af", $"apad=pad_dur={padSeconds}", "-ac", "1", "-ar", "16000", "-f", "f32le", "pipe:1"])
            psi.ArgumentList.Add(a);
        using var pcm = new MemoryStream();
        using var ffmpeg = Process.Start(psi)!;
        await ffmpeg.StandardOutput.BaseStream.CopyToAsync(pcm, TestContext.Current.CancellationToken);
        await ffmpeg.WaitForExitAsync(TestContext.Current.CancellationToken);
        return pcm.ToArray();
    }

    [Fact]
    public async Task Whisper_transcribes_speech_with_word_times()
    {
        var model = ModelCatalog.Find("whisper-base.en")!;
        string? dir = ModelDirectory(model);
        Assert.SkipWhen(dir is null, "whisper-base.en is not installed in OURCUT_MODELS_DIR.");
        byte[] pcm = await Pcm(Path.Combine(dir!, "test_wavs", "0.wav"));
        using var audio = new MemoryStream(pcm);

        using var recognizer = new SherpaRecognizer(model, dir!, ModelCatalog.OnlyLanguage(model));
        var words = await TranscriptionPipeline.RunAsync(audio, pcm.Length / 4.0 / 16000, recognizer, cancellationToken: TestContext.Current.CancellationToken);

        string text = string.Join(' ', words.Select(w => w.Text));
        Assert.Contains("yellow lamps would light up", text, StringComparison.OrdinalIgnoreCase);
        // These models give no word times: they are estimated from the audio, in order and within the speech.
        Assert.True(recognizer.HasApproximateTimes);
        Assert.True(words.Zip(words.Skip(1)).All(p => p.Second.Start >= p.First.End - 1e-9), text);
        Assert.InRange(words[0].Start, 0, 1);
        Assert.InRange(words[^1].End, 5, 7);
    }

    [Fact]
    public async Task Parakeet_transcribes_speech_with_word_times()
    {
        string? dir = ModelDirectory(ModelCatalog.Parakeet);
        Assert.SkipWhen(dir is null, "Parakeet is not installed in OURCUT_MODELS_DIR.");
        // The model's own sample, three times with pauses: pieces and offsets are exercised too.
        byte[] pcm = await Pcm(Path.Combine(dir!, "test_wavs", "en.wav"), 12);
        using var audio = new MemoryStream([.. pcm, .. pcm, .. pcm]);
        double duration = pcm.Length / 4.0 / 16000 * 3;

        using var recognizer = new SherpaRecognizer(ModelCatalog.Parakeet, dir!);
        var words = await TranscriptionPipeline.RunAsync(audio, duration, recognizer, cancellationToken: TestContext.Current.CancellationToken);

        string text = string.Join(' ', words.Select(w => w.Text)).ToLowerInvariant();
        string timed = string.Join(" ", words.Select(w => $"{w.Text}@{w.Start:0.00}-{w.End:0.00}"));
        Assert.True(text.Split("ask not what your country can do for you").Length - 1 == 3, timed);
        var ask = words.Where((w, i) => w.Text.Equals("Ask", StringComparison.OrdinalIgnoreCase) && i + 1 < words.Count && words[i + 1].Text == "not")
            .Select(w => w.Start).ToList();
        Assert.Equal(3, ask.Count);
        Assert.Equal(ask[0] + duration / 3, ask[1], 0.3);
        Assert.Equal(ask[0] + 2 * duration / 3, ask[2], 0.3);
        Assert.All(words, w => Assert.True(w.End >= w.Start));
    }
}
