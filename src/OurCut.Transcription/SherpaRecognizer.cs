using OurCut.Core.Transcripts;
using OurCut.Transcription.Models;
using SherpaOnnx;

namespace OurCut.Transcription;

/// <summary>
/// Speech recognition with sherpa-onnx (ONNX Runtime on the CPU): Parakeet TDT through its transducer, Whisper
/// through its encoder/decoder with token timestamps. Both give subword tokens with start times, turned into words
/// by <see cref="WordBuilder"/>.
/// </summary>
public sealed class SherpaRecognizer : ISpeechRecognizer
{
    private readonly OfflineRecognizer _recognizer;

    /// <param name="directory">The installed model's folder.</param>
    /// <param name="language">Two-letter code for Whisper ("en", "uk"), or null to detect it. Parakeet always detects.</param>
    /// <param name="threads">CPU threads; half the cores by default, so the editor stays responsive.</param>
    public SherpaRecognizer(TranscriptionModel model, string directory, string? language = null, int? threads = null)
    {
        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = TranscriptionPipeline.SampleRate;
        config.FeatConfig.FeatureDim = 80;
        config.DecodingMethod = "greedy_search";
        config.ModelConfig.NumThreads = threads ?? Math.Max(1, Environment.ProcessorCount / 2);
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.Tokens = Path.Combine(directory, model.Files.Single(f => f.EndsWith("tokens.txt", StringComparison.Ordinal)));
        string File(string part) => Path.Combine(directory, model.Files.Single(f => f.Contains(part, StringComparison.Ordinal)));
        switch (model.Engine)
        {
            case TranscriptionEngine.Parakeet:
                config.ModelConfig.ModelType = "nemo_transducer";
                config.ModelConfig.Transducer.Encoder = File("encoder");
                config.ModelConfig.Transducer.Decoder = File("decoder");
                config.ModelConfig.Transducer.Joiner = File("joiner");
                break;
            case TranscriptionEngine.Whisper:
                config.ModelConfig.Whisper.Encoder = File("encoder");
                config.ModelConfig.Whisper.Decoder = File("decoder");
                config.ModelConfig.Whisper.Language = language ?? "";
                config.ModelConfig.Whisper.Task = "transcribe";
                config.ModelConfig.Whisper.TailPaddings = -1;
                // Only models exported with attention outputs have token times; the others get estimated ones.
                config.ModelConfig.Whisper.EnableTokenTimestamps = 0;
                break;
        }
        _recognizer = new OfflineRecognizer(config);
    }

    public IReadOnlyList<Word> Recognize(float[] samples, double offset)
    {
        using var stream = _recognizer.CreateStream();
        stream.AcceptWaveform(TranscriptionPipeline.SampleRate, samples);
        _recognizer.Decode(stream);
        var result = stream.Result;
        if (result.Tokens is not { Length: > 0 } tokens)
            return [];
        if (result.Timestamps is { } times && times.Length == tokens.Length)
            return WordBuilder.Build(tokens, times, offset, offset + (double)samples.Length / TranscriptionPipeline.SampleRate);
        HasApproximateTimes = true;
        return WordBuilder.Estimate(WordBuilder.Texts(tokens), samples, offset);
    }

    /// <summary>Some words had no times from the model and were given estimated ones.</summary>
    public bool HasApproximateTimes { get; private set; }

    public void Dispose() => _recognizer.Dispose();
}
