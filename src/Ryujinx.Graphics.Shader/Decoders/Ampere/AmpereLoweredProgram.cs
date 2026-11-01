using System.Collections.Generic;

namespace Ryujinx.Graphics.Shader.Decoders.Ampere
{
    /// <summary>
    /// The result of lowering a native Ampere program to Maxwell instructions.
    /// </summary>
    public sealed class AmpereLoweredProgram
    {
        public List<ulong> Instructions { get; } = [];

        /// <summary>
        /// For each native instruction, the index of its lowered word in <see cref="Instructions"/>.
        /// </summary>
        public List<uint> SourceInstructionOffsets { get; } = [];

        public uint SourceSize { get; set; }

        /// <summary>
        /// Texture descriptor buffer the lowered program expects, or <see cref="uint.MaxValue"/>.
        /// </summary>
        public uint RequiredTextureBuffer { get; set; } = uint.MaxValue;

        public uint ErrorOffset { get; set; }

        /// <summary>
        /// Lowering failure reason, or null on success.
        /// </summary>
        public string Error { get; set; }

        public bool Success => Error == null;
    }
}
