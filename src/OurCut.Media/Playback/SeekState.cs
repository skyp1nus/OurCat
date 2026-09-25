namespace OurCut.Media.Playback;

/// <summary>
/// Which seeks mpv has finished. Seeks are numbered; mpv replies to each seek command and then restarts
/// playback. A restart settles every seek replied to so far, and <see cref="IsSeeking"/> holds while a newer
/// one is still in flight. Everything is a generation number that only grows, so a restart left over from an
/// earlier seek (or from loading the file) can never mark a newer seek as done, whatever the thread timing.
/// </summary>
internal sealed class SeekState
{
    private long _requested, _replied, _settled;

    public bool IsSeeking => Volatile.Read(ref _settled) < Volatile.Read(ref _requested);

    /// <summary>A new seek; returns its number.</summary>
    public long Request() => Interlocked.Increment(ref _requested);

    /// <summary>mpv replied to seek <paramref name="generation"/>. A failed seek brings no restart, so it settles now.</summary>
    public void Replied(long generation, bool failed)
    {
        Max(ref _replied, generation);
        if (failed)
            Max(ref _settled, generation);
    }

    /// <summary>Playback restarted: the seeks replied to so far are done.</summary>
    public void Restarted() => Max(ref _settled, Volatile.Read(ref _replied));

    private static void Max(ref long field, long value)
    {
        long current = Volatile.Read(ref field);
        while (value > current)
        {
            long seen = Interlocked.CompareExchange(ref field, value, current);
            if (seen == current)
                return;
            current = seen;
        }
    }
}
