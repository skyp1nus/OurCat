using System.Buffers.Binary;

namespace OurCut.Transcription;

/// <summary>Recognizes speech in short pieces of 16 kHz mono audio.</summary>
public interface ISpeechRecognizer : IDisposable
{
    /// <summary>The words in <paramref name="samples"/>, timed on the source timeline (the piece starts at <paramref name="offset"/>).</summary>
    IReadOnlyList<Word> Recognize(float[] samples, double offset);

    /// <summary>Some word times were estimated from the audio because the model gave none.</summary>
    bool HasApproximateTimes => false;
}

/// <summary>
/// Reads 16 kHz mono float audio as it is decoded, cuts it into pieces of at most half a minute at the quietest
/// moment near the end of each (so words are not cut in half) and recognizes the pieces one after another.
/// Memory stays small however long the recording is.
/// </summary>
public static class TranscriptionPipeline
{
    public const int SampleRate = 16000;

    /// <summary>Longest piece given to the recognizer, in seconds (Whisper takes at most 30 s).</summary>
    public const double MaxPiece = 28;

    /// <summary>A piece is cut at the quietest moment after this many seconds.</summary>
    public const double MinPiece = 18;

    /// <param name="audio">Raw little-endian 32-bit float samples, 16 kHz mono (ffmpeg <c>-f f32le</c>).</param>
    /// <param name="duration">Length of the audio in seconds, for progress.</param>
    /// <param name="onPiece">Called after each piece with its words and the progress 0..1.</param>
    public static async Task<List<Word>> RunAsync(Stream audio, double duration, ISpeechRecognizer recognizer,
        Action<IReadOnlyList<Word>, double>? onPiece = null, CancellationToken cancellationToken = default)
    {
        int max = (int)(MaxPiece * SampleRate), min = (int)(MinPiece * SampleRate);
        var buffer = new float[max];
        int filled = 0;
        long consumed = 0;
        var words = new List<Word>();
        var bytes = new byte[64 * 1024];
        int carry = 0;

        void Emit(int count)
        {
            var piece = buffer[..count];
            var found = recognizer.Recognize(piece, (double)consumed / SampleRate);
            words.AddRange(found);
            consumed += count;
            Array.Copy(buffer, count, buffer, 0, filled - count);
            filled -= count;
            onPiece?.Invoke(found, duration > 0 ? Math.Min(1, consumed / (duration * SampleRate)) : 0);
        }

        while (true)
        {
            int read = await audio.ReadAsync(bytes.AsMemory(carry), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            int available = carry + read;
            int whole = available / 4 * 4;
            for (int off = 0; off < whole; off += 4)
            {
                buffer[filled++] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(off, 4));
                if (filled == max)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Emit(QuietestCut(buffer, min, max));
                }
            }
            carry = available - whole;
            if (carry > 0)
                Buffer.BlockCopy(bytes, whole, bytes, 0, carry);
        }
        if (filled > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Emit(filled);
        }
        return words;
    }

    /// <summary>
    /// Index in [<paramref name="from"/>, <paramref name="to"/>) at the middle of the quietest 100 ms: the best place
    /// to end a piece.
    /// </summary>
    public static int QuietestCut(ReadOnlySpan<float> samples, int from, int to)
    {
        const int Window = SampleRate / 10, Step = SampleRate / 100;
        int best = to;
        double quietest = double.MaxValue;
        for (int start = from; start + Window <= to; start += Step)
        {
            double energy = 0;
            for (int i = start; i < start + Window; i++)
                energy += samples[i] * samples[i];
            if (energy < quietest)
            {
                quietest = energy;
                best = start + Window / 2;
            }
        }
        return best;
    }
}
