namespace DesktopPet
{
    /// <summary>
    /// Copied from the base RuntimeGeometry.cs (S4) so the relocated AI-brain engine has no dependency on
    /// the base assembly. Kept in namespace DesktopPet so the relocated DesktopPet.Ai code (AiBrain OCR
    /// truncation, ChatHistory summary bounding) resolves it by simple name, exactly as it did in the base.
    /// </summary>
    internal static class UnicodeTextProgress
    {
        public static int NextCodePointBoundary(string text, int currentLength)
        {
            text = text ?? "";
            if (currentLength < 0) currentLength = 0;
            if (currentLength >= text.Length) return text.Length;
            if (char.IsHighSurrogate(text[currentLength]) &&
                currentLength + 1 < text.Length &&
                char.IsLowSurrogate(text[currentLength + 1]))
                return currentLength + 2;
            return currentLength + 1;
        }

        public static string TruncateAtCodePointBoundary(
            string text,
            int maximumCodeUnits)
        {
            text = text ?? "";
            if (maximumCodeUnits <= 0) return "";
            if (text.Length <= maximumCodeUnits) return text;

            int length = maximumCodeUnits;
            if (length > 0 &&
                char.IsHighSurrogate(text[length - 1]) &&
                length < text.Length &&
                char.IsLowSurrogate(text[length]))
                length--;
            return text.Substring(0, length);
        }
    }
}
