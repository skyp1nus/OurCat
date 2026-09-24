using System.Buffers.Binary;
using System.Globalization;
using OurCut.Media.Probing;
using OurCut.Media.Tools;

namespace OurCut.Media.Previews;

/// <summary>
/// Audio peaks of every audio stream, 100 buckets per second, filled progressively while ffmpeg decodes.
/// Reading while it fills is safe: <see cref="Filled"/> only grows.
/// </summary>
public sealed class WaveformData
{
    public const int BucketsPerSecond = 100;

    private readonly float[][] _peaks;
    private int _filled;

    public WaveformData(int streams, double duration)
    {
        int buckets = (int)Math.Ceiling(Math.Max(0, duration) * BucketsPerSecond) + 1;
        _peaks = [.. Enumerable.Range(0, streams).Select(_ => new float[buckets])];
    }

    internal WaveformData(float[][] peaks, int filled)
    {
        _peaks = peaks;
        _filled = filled;
    }

    public int StreamCount => _peaks.Length;
    public int Capacity => _peaks.Length == 0 ? 0 : _peaks[0].Length;

    /// <summary>Number of buckets decoded so far.</summary>
    public int Filled => Volatile.Read(ref _filled);

    public bool IsComplete { get; internal set; }

    /// <summary>Raw peak (linear, 0..1) of one bucket.</summary>
    public float this[int stream, int bucket] => _peaks[stream][bucket];

    internal float[][] RawPeaks => _peaks;

    internal void Set(int stream, int bucket, float peak)
    {
        if (bucket < _peaks[stream].Length)
            _peaks[stream][bucket] = peak;
    }

    internal void Publish(int filled) => Volatile.Write(ref _filled, Math.Min(filled, Capacity));

    /// <summary>Takes over the peaks of another waveform of the same file, e.g. one read from the cache.</summary>
    public void CopyFrom(WaveformData other)
    {
        ArgumentNullException.ThrowIfNull(other);
        int n = Math.Min(Capacity, other.Capacity);
        for (int s = 0; s < Math.Min(StreamCount, other.StreamCount); s++)
            Array.Copy(other._peaks[s], _peaks[s], n);
        Publish(Math.Min(other.Filled, n));
        IsComplete = other.IsComplete;
    }

    /// <summary>
    /// Display height 0..1 of the loudest bucket between two times, on a dB scale (−48 dB → 0) so quiet
    /// speech is still visible.
    /// </summary>
    public double Peak(int stream, double start, double end)
    {
        if (stream < 0 || stream >= _peaks.Length)
            return 0;
        int filled = Filled;
        int b0 = Math.Max(0, (int)Math.Floor(start * BucketsPerSecond));
        int b1 = Math.Min(filled, Math.Max(b0 + 1, (int)Math.Ceiling(end * BucketsPerSecond)));
        float max = 0;
        var p = _peaks[stream];
        for (int b = b0; b < b1; b++)
            max = Math.Max(max, p[b]);
        return ToDisplay(max);
    }

    public static double ToDisplay(double linear) =>
        linear <= 0 ? 0 : Math.Clamp(1 + 20 * Math.Log10(linear) / 48, 0, 1);
}

/// <summary>
/// Decodes all audio streams in one ffmpeg pass (each resampled to 8 kHz mono and merged into one
/// interleaved stream) and folds the samples into <see cref="WaveformData"/> peaks.
/// </summary>
public static class WaveformExtractor
{
    public const int SampleRate = 8000;
    private const int SamplesPerBucket = SampleRate / WaveformData.BucketsPerSecond;

    public static WaveformData Create(MediaInfo info) => new(info.Audio.Length, info.Duration);

    public static async Task ExtractAsync(MediaInfo info, WaveformData target, Action? onChunk = null,
        CancellationToken cancellationToken = default)
    {
        int n = info.Audio.Length;
        if (n == 0)
        {
            target.IsComplete = true;
            return;
        }
        await ToolProcess.RunAsync("ffmpeg", Arguments(info), async (stdout, ct) =>
        {
            var buffer = new byte[64 * 1024];
            int frameBytes = 2 * n, carry = 0, sampleInBucket = 0, bucket = 0;
            var peaks = new float[n];
            var lastPublish = DateTime.UtcNow;
            while (true)
            {
                int read = await stdout.ReadAsync(buffer.AsMemory(carry, buffer.Length - carry), ct).ConfigureAwait(false);
                if (read == 0)
                    break;
                int available = carry + read;
                int whole = available / frameBytes * frameBytes;
                for (int off = 0; off < whole; off += frameBytes)
                {
                    for (int s = 0; s < n; s++)
                    {
                        short v = BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(off + 2 * s, 2));
                        float a = Math.Abs(v / 32768f);
                        if (a > peaks[s])
                            peaks[s] = a;
                    }
                    if (++sampleInBucket == SamplesPerBucket)
                    {
                        for (int s = 0; s < n; s++)
                        {
                            target.Set(s, bucket, peaks[s]);
                            peaks[s] = 0;
                        }
                        bucket++;
                        sampleInBucket = 0;
                    }
                }
                carry = available - whole;
                if (carry > 0)
                    Buffer.BlockCopy(buffer, whole, buffer, 0, carry);
                if ((DateTime.UtcNow - lastPublish).TotalMilliseconds > 250)
                {
                    target.Publish(bucket);
                    onChunk?.Invoke();
                    lastPublish = DateTime.UtcNow;
                }
            }
            if (sampleInBucket > 0)
            {
                for (int s = 0; s < n; s++)
                    target.Set(s, bucket, peaks[s]);
                bucket++;
            }
            target.Publish(bucket);
        }, cancellationToken).ConfigureAwait(false);
        target.IsComplete = true;
        onChunk?.Invoke();
    }

    /// <summary>ffmpeg arguments: one 8 kHz mono channel per audio stream, interleaved s16le on stdout.</summary>
    public static IReadOnlyList<string> Arguments(MediaInfo info)
    {
        var args = new List<string> { "-v", "error", "-i", info.Path, "-vn", "-sn", "-dn" };
        const string Mono = "aresample={0},aformat=sample_fmts=s16:channel_layouts=mono";
        string mono = string.Format(CultureInfo.InvariantCulture, Mono, SampleRate);
        if (info.Audio.Length == 1)
        {
            args.AddRange(["-map", $"0:{info.Audio[0].Index}", "-af", mono]);
        }
        else
        {
            var graph = string.Join(';', info.Audio.Select((a, i) => $"[0:{a.Index}]{mono}[m{i}]"))
                + ";" + string.Concat(info.Audio.Select((_, i) => $"[m{i}]"))
                + $"amerge=inputs={info.Audio.Length}[out]";
            args.AddRange(["-filter_complex", graph, "-map", "[out]"]);
        }
        args.AddRange(["-c:a", "pcm_s16le", "-f", "s16le", "pipe:1"]);
        return args;
    }
}
