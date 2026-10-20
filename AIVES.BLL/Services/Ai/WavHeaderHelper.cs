using System.Text;

namespace AIVES.BLL.Services.Ai;

/// <summary>
/// Utility for wrapping raw PCM linear audio bytes with a standard 44-byte RIFF/WAVE header.
/// Used for Gemini TTS audio synthesis output (raw 16-bit 24kHz mono PCM).
/// </summary>
public static class WavHeaderHelper
{
    public const int HeaderSizeBytes = 44;

    /// <summary>
    /// Wraps raw 16-bit PCM audio samples with a standard 44-byte WAV header.
    /// </summary>
    /// <param name="pcmData">Raw linear PCM bytes (16-bit, little-endian).</param>
    /// <param name="sampleRate">Sampling rate in Hz (default: 24,000 for Gemini).</param>
    /// <param name="channels">Number of audio channels (default: 1 for mono).</param>
    /// <param name="bitsPerSample">Bits per audio sample (default: 16).</param>
    /// <returns>Complete WAV file binary payload.</returns>
    public static byte[] WrapPcm16(byte[] pcmData, int sampleRate = 24000, short channels = 1, short bitsPerSample = 16)
    {
        ArgumentNullException.ThrowIfNull(pcmData);

        var pcmLength = pcmData.Length;
        var totalDataLen = pcmLength + 36;
        var byteRate = sampleRate * channels * (bitsPerSample / 8);
        var blockAlign = (short)(channels * (bitsPerSample / 8));

        var wav = new byte[HeaderSizeBytes + pcmLength];
        using var ms = new MemoryStream(wav);
        using var writer = new BinaryWriter(ms, Encoding.UTF8);

        // RIFF header
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write((uint)totalDataLen);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));

        // fmt subchunk
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16); // Subchunk1Size for PCM
        writer.Write((short)1); // AudioFormat: 1 = PCM
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);

        // data subchunk
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write((uint)pcmLength);

        // Copy raw PCM payload
        Buffer.BlockCopy(pcmData, 0, wav, HeaderSizeBytes, pcmLength);

        return wav;
    }
}
