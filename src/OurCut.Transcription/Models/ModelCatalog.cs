using System.Globalization;

namespace OurCut.Transcription.Models;

public enum TranscriptionEngine
{
    /// <summary>NVIDIA Parakeet TDT through sherpa-onnx (ONNX Runtime).</summary>
    Parakeet,

    /// <summary>OpenAI Whisper through whisper.cpp.</summary>
    Whisper,
}

/// <summary>How a model is published.</summary>
public enum ModelPackage
{
    /// <summary>One file, downloaded as is.</summary>
    SingleFile,

    /// <summary>A .tar.bz2 archive with one top folder (sherpa-onnx releases), unpacked without that folder.</summary>
    TarBz2,
}

/// <summary>A speech-to-text model OurCut can download.</summary>
/// <param name="Id">Folder name under the models folder, e.g. "whisper-small".</param>
/// <param name="Languages">What it understands, for the model table.</param>
/// <param name="DownloadSize">Approximate download size in bytes (the server's size is used once known).</param>
/// <param name="Files">Files that must be there once installed.</param>
public sealed record TranscriptionModel(
    string Id,
    TranscriptionEngine Engine,
    string Languages,
    long DownloadSize,
    Uri Url,
    ModelPackage Package,
    IReadOnlyList<string> Files)
{
    /// <summary>"487 MB", "1.6 GB".</summary>
    public string SizeText => FormatSize(DownloadSize);

    public static string FormatSize(long bytes) =>
        bytes >= 1_000_000_000
            ? (bytes / 1e9).ToString("0.0", CultureInfo.InvariantCulture) + " GB"
            : Math.Max(1, Math.Round(bytes / 1e6)).ToString("0", CultureInfo.InvariantCulture) + " MB";
}

/// <summary>The models listed in Settings → Transcription, best first.</summary>
public static class ModelCatalog
{
    private const string WhisperBase = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";

    public static TranscriptionModel Parakeet { get; } = new(
        "parakeet-tdt-0.6b-v3", TranscriptionEngine.Parakeet, "25 European languages", 487_170_055,
        new Uri("https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8.tar.bz2"),
        ModelPackage.TarBz2, ["encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx", "tokens.txt"]);

    public static IReadOnlyList<TranscriptionModel> All { get; } =
    [
        Parakeet,
        Whisper("whisper-large-v3-turbo", "ggml-large-v3-turbo.bin", 1_624_555_275, "99 languages"),
        Whisper("whisper-medium", "ggml-medium.bin", 1_533_763_059, "99 languages"),
        Whisper("whisper-small", "ggml-small.bin", 487_601_967, "99 languages"),
        Whisper("whisper-base.en", "ggml-base.en.bin", 147_964_211, "English"),
    ];

    public static TranscriptionModel? Find(string id) => All.FirstOrDefault(m => m.Id == id);

    private static TranscriptionModel Whisper(string id, string file, long size, string languages) =>
        new(id, TranscriptionEngine.Whisper, languages, size, new Uri(WhisperBase + file), ModelPackage.SingleFile, [file]);
}
