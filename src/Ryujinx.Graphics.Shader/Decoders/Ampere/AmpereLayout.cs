namespace Ryujinx.Graphics.Shader.Decoders.Ampere
{
    /// <summary>
    /// Maxwell code layout used by Ryujing's decoder.
    /// </summary>
    /// <remarks>
    /// The decoder skips one scheduling word at every code-relative 32-byte boundary,
    /// including offset zero (see <c>Decoder.FillBlock</c>). Lowered instructions therefore
    /// start at offset 8 and are packed three per 32-byte block.
    /// </remarks>
    public static class AmpereLayout
    {
        public const int HeaderSize = 0x50;

        public static uint AddressOf(uint instructionIndex)
        {
            return 8 + instructionIndex * 8 + (instructionIndex / 3) * 8;
        }
    }
}
