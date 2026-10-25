namespace DeveLiveStreamer.Streaming.Audio;

/// <summary>
/// Mixes 16-bit PCM inputs into one interleaved output format. Each input converts channels and sample rate on write
/// and keeps a bounded buffer, so a slow or bursty source cannot grow the delay without limit.
/// </summary>
public sealed class AudioMixer(int sampleRate, int channels)
{
    private readonly List<AudioMixerInput> inputs = [];
    private float[] sum = [];

    public int SampleRate { get; } = sampleRate;
    public int Channels { get; } = channels;

    public AudioMixerInput AddInput(string name, int maximumBufferMs = 400)
    {
        var input = new AudioMixerInput(name, SampleRate, Channels, maximumBufferMs);
        lock (inputs) inputs.Add(input);
        return input;
    }

    public void RemoveInput(AudioMixerInput input) { lock (inputs) inputs.Remove(input); }

    /// <summary>Fills <paramref name="output"/> (interleaved) with the sum of all inputs; missing samples are silence.</summary>
    public void Mix(Span<short> output)
    {
        if (sum.Length < output.Length) sum = new float[output.Length];
        var accumulator = sum.AsSpan(0, output.Length);
        accumulator.Clear();
        AudioMixerInput[] current;
        lock (inputs) current = inputs.ToArray();
        foreach (var input in current) input.ReadInto(accumulator);
        for (var i = 0; i < output.Length; i++) output[i] = (short)Math.Clamp(accumulator[i], short.MinValue, short.MaxValue);
    }
}

public sealed class AudioMixerInput
{
    private readonly int sampleRate, channels, capacity;
    private readonly float[] ring;
    private int read, count;
    private double position; // fractional source position for resampling, carried across writes
    private float[] previous = [];
    private bool started, primed;

    internal AudioMixerInput(string name, int sampleRate, int channels, int maximumBufferMs)
    {
        Name = name; this.sampleRate = sampleRate; this.channels = channels;
        capacity = Math.Max(1, sampleRate * maximumBufferMs / 1000) * channels;
        ring = new float[capacity];
    }

    public string Name { get; }
    public float Gain { get; set; } = 1;
    public bool Muted { get; set; }
    public int BufferedFrames { get { lock (ring) return count / channels; } }

    public void Write(ReadOnlySpan<short> interleaved, int sourceRate, int sourceChannels)
    {
        if (sourceRate <= 0 || sourceChannels <= 0) return;
        var frames = interleaved.Length / sourceChannels;
        if (frames == 0) return;
        lock (ring)
        {
            if (previous.Length != channels) { previous = new float[channels]; started = false; }
            var step = (double)sourceRate / sampleRate;
            Span<float> frame = stackalloc float[channels];
            // Linear interpolation between the previous call's last frame and this call's frames.
            while (position < frames)
            {
                var index = (int)Math.Floor(position);
                var fraction = (float)(position - index);
                for (var c = 0; c < channels; c++)
                {
                    var next = Sample(interleaved, index, c, sourceChannels);
                    var before = index == 0 ? (started ? previous[c] : next) : Sample(interleaved, index - 1, c, sourceChannels);
                    frame[c] = fraction == 0 ? before : before + (next - before) * fraction;
                }
                Append(frame);
                position += step;
            }
            position -= frames;
            for (var c = 0; c < channels; c++) previous[c] = Sample(interleaved, frames - 1, c, sourceChannels);
            started = true;
        }
    }

    private float Sample(ReadOnlySpan<short> data, int frame, int channel, int sourceChannels)
    {
        if (sourceChannels == channels) return data[frame * sourceChannels + channel];
        if (channels == 1)
        {
            float total = 0;
            for (var c = 0; c < sourceChannels; c++) total += data[frame * sourceChannels + c];
            return total / sourceChannels;
        }
        return data[frame * sourceChannels + Math.Min(channel, sourceChannels - 1)];
    }

    private void Append(ReadOnlySpan<float> frame)
    {
        if (count + frame.Length > capacity)
        {
            // Over the bound: drop the oldest half so the delay settles back near a small buffer.
            var drop = Math.Max(frame.Length, count / 2 / channels * channels);
            read = (read + drop) % capacity; count -= drop;
        }
        foreach (var value in frame) { ring[(read + count) % capacity] = value; count++; }
    }

    internal void ReadInto(Span<float> destination)
    {
        lock (ring)
        {
            // After running dry, wait for a small cushion before playing again: one gap instead of many.
            if (!primed && count < Math.Min(capacity / 2, destination.Length / 2)) return;
            primed = count >= destination.Length;
            var available = Math.Min(count, destination.Length);
            var gain = Muted ? 0 : Gain;
            for (var i = 0; i < available; i++) destination[i] += ring[(read + i) % capacity] * gain;
            read = (read + available) % capacity; count -= available;
        }
    }

    public void Clear() { lock (ring) { read = count = 0; position = 0; started = primed = false; } }
}
