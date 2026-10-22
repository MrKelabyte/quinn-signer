using System;
using System.Collections.Generic;

namespace Features.Signing.Recognition
{
    /// <summary>
    /// Anything that can turn a player's signing into a word.
    ///
    /// Signer only talks to this interface, never to a concrete engine. That way the
    /// recognition engine can be swapped (dev keys today, a camera-based engine later)
    /// by changing one component in the Inspector, with no gameplay code changes.
    /// </summary>
    public interface ISignRecognizer
    {
        /// <summary>Raised when a sign has been classified. Word is the raw label, confidence is 0..1.</summary>
        event Action<string, float> SignRecognized;

        /// <summary>True while a sign is being captured (e.g. the camera is recording).</summary>
        bool IsCapturing { get; }

        /// <summary>Start capturing a sign (player pressed and is holding the sign button).</summary>
        void BeginCapture();

        /// <summary>Stop capturing and classify what was captured. Raises <see cref="SignRecognized"/>.</summary>
        void EndCapture();

        /// <summary>Stop capturing and discard it. Raises nothing.</summary>
        void CancelCapture();

        /// <summary>
        /// Limit recognition to these words (the ones currently in play).
        /// An empty list means "no limit".
        /// </summary>
        void SetVocabulary(IReadOnlyList<string> words);
    }
}
