namespace NodeX.Core.Audio;

/// <summary>
/// The slice of a call session the audio engine needs: outbound µ-law frames while keyed,
/// and inbound timestamped µ-law frames. Implemented by <see cref="Iax2Session"/>.
/// </summary>
public interface IVoiceChannel
{
    /// <summary>Whether local PTT is keyed (outbound audio should be sent).</summary>
    bool IsTransmitting { get; }

    /// <summary>
    /// Raised (on any thread) after <see cref="IsTransmitting"/> changes. Lets the audio engine open the
    /// microphone only while keyed. The default implementation never raises it.
    /// </summary>
    event Action<bool>? TransmittingChanged { add { } remove { } }

    /// <summary>Inbound µ-law payload with its 16-bit millisecond timestamp.</summary>
    event Action<ushort, ReadOnlyMemory<byte>>? VoiceFrameReceived;

    /// <summary>Sends one 20 ms µ-law frame.</summary>
    Task SendAudioFrameAsync(ReadOnlyMemory<byte> ulawAudio, CancellationToken cancellationToken = default);
}
