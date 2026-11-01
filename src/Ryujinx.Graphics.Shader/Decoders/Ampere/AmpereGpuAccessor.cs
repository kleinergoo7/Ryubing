using Ryujinx.Graphics.Shader.CodeGen;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Shader.Decoders.Ampere
{
    /// <summary>
    /// Serves a lowered Maxwell shader image built from a native Ampere (SM86) program.
    /// </summary>
    /// <remarks>
    /// The guest header is kept for graphics stages, so <see cref="Ryujinx.Graphics.Shader.Translation.ShaderHeader"/>
    /// parses the same bytes as before. Only <see cref="GetCode"/> sees the lowered code.
    /// Everything else is forwarded to the wrapped accessor.
    /// </remarks>
    public sealed class AmpereGpuAccessor : IGpuAccessor
    {
        private readonly IGpuAccessor _inner;
        private readonly byte[] _image;
        private readonly ulong[] _codeWords;
        private readonly ulong _baseAddress;
        private readonly AmpereLoweredProgram _lowered;

        private AmpereGpuAccessor(
            IGpuAccessor inner,
            AmpereLoweredProgram lowered,
            byte[] image,
            ulong address,
            bool hasHeader)
        {
            _inner = inner;
            _lowered = lowered;
            _image = image;
            _baseAddress = address;
            _codeWords = NativeShaderImage.ReadCodeWords(image, hasHeader);
        }

        /// <summary>
        /// Size in bytes of the native program, without the graphics header.
        /// </summary>
        public uint NativeSize => _lowered.SourceSize;

        /// <summary>
        /// Builds the lowered image and wraps <paramref name="inner"/>.
        /// Returns null and sets <paramref name="error"/> when the program cannot be lowered.
        /// </summary>
        public static AmpereGpuAccessor Create(
            IGpuAccessor inner,
            ulong address,
            bool hasHeader,
            out string error)
        {
            int headerSize = hasHeader ? AmpereLayout.HeaderSize : 0;

            byte[] guestHeader = [];

            if (hasHeader)
            {
                ReadOnlySpan<ulong> headerWords = inner.GetCode(address, AmpereLayout.HeaderSize);
                ReadOnlySpan<byte> headerBytes = MemoryMarshal.AsBytes(headerWords);

                if (headerBytes.Length < AmpereLayout.HeaderSize)
                {
                    error = "native shader header is shorter than 0x50 bytes";
                    return null;
                }

                guestHeader = headerBytes[..AmpereLayout.HeaderSize].ToArray();
            }

            if (!Capture(inner, address + (ulong)headerSize, out AmpereInstruction[] instructions, out error))
            {
                return null;
            }

            AmpereLoweredProgram lowered = AmpereLowering.LowerToMaxwell(instructions);

            if (!lowered.Success)
            {
                error = $"native lowering failed at 0x{lowered.ErrorOffset:X}: {lowered.Error}";
                return null;
            }

            byte[] image = NativeShaderImage.Build(guestHeader, lowered, hasHeader);

            error = null;

            return new AmpereGpuAccessor(inner, lowered, image, address, hasHeader);
        }

        /// <summary>
        /// Maps a native program counter (byte offset) to the lowered Maxwell code offset.
        /// </summary>
        public uint? FindNativeAddress(uint address)
        {
            return NativeAddressMap.FindAddress(_codeWords, address);
        }

        /// <inheritdoc/>
        public ReadOnlySpan<ulong> GetCode(ulong address, int minimumSize)
        {
            if (address < _baseAddress)
            {
                return ReadOnlySpan<ulong>.Empty;
            }

            long offset = (long)(address - _baseAddress);

            if (offset >= _image.Length)
            {
                return ReadOnlySpan<ulong>.Empty;
            }

            int length = (_image.Length - (int)offset) & ~7;

            return MemoryMarshal.Cast<byte, ulong>(_image.AsSpan((int)offset, length));
        }

        private static bool Capture(
            IGpuAccessor inner,
            ulong codeAddress,
            out AmpereInstruction[] instructions,
            out string error)
        {
            List<AmpereInstruction> captured = [];
            AmpereProgramCapture capture = new();
            ulong offset = 0;

            while (captured.Count < AmpereProgramCapture.MaximumInstructions)
            {
                ReadOnlySpan<ulong> span = inner.GetCode(codeAddress + offset, AmpereInstruction.Size);
                int available = span.Length / 2;

                if (available == 0)
                {
                    break;
                }

                int consumed = 0;
                bool complete = false;

                for (int index = 0; index < available; index++)
                {
                    AmpereInstruction instruction = AmpereInstruction.Read(
                        MemoryMarshal.AsBytes(span.Slice(index * 2, 2)));

                    captured.Add(instruction);
                    consumed++;

                    AmpereProgramCapture.Result result = capture.Observe(instruction, (uint)captured.Count - 1);

                    if (result == AmpereProgramCapture.Result.InvalidTarget)
                    {
                        instructions = null;
                        error = $"native branch target is invalid at instruction {captured.Count - 1}";
                        return false;
                    }

                    if (result == AmpereProgramCapture.Result.Complete)
                    {
                        complete = true;
                        break;
                    }
                }

                offset += (ulong)(consumed * AmpereInstruction.Size);

                if (complete)
                {
                    instructions = captured.ToArray();
                    error = null;
                    return true;
                }
            }

            instructions = null;
            error = "native program end not found";
            return false;
        }

        /// <inheritdoc/>
        public void Log(string message) => _inner.Log(message);

        /// <inheritdoc/>
        public uint ConstantBuffer1Read(int offset) => _inner.ConstantBuffer1Read(offset);

        /// <inheritdoc/>
        public SetBindingPair CreateConstantBufferBinding(int index) => _inner.CreateConstantBufferBinding(index);

        /// <inheritdoc/>
        public SetBindingPair CreateImageBinding(int count, bool isBuffer) => _inner.CreateImageBinding(count, isBuffer);

        /// <inheritdoc/>
        public SetBindingPair CreateStorageBufferBinding(int index) => _inner.CreateStorageBufferBinding(index);

        /// <inheritdoc/>
        public SetBindingPair CreateTextureBinding(int count, bool isBuffer) => _inner.CreateTextureBinding(count, isBuffer);

        /// <inheritdoc/>
        public int CreateExtraSet() => _inner.CreateExtraSet();

        /// <inheritdoc/>
        public int QueryComputeLocalSizeX() => _inner.QueryComputeLocalSizeX();

        /// <inheritdoc/>
        public int QueryComputeLocalSizeY() => _inner.QueryComputeLocalSizeY();

        /// <inheritdoc/>
        public int QueryComputeLocalSizeZ() => _inner.QueryComputeLocalSizeZ();

        /// <inheritdoc/>
        public int QueryComputeLocalMemorySize() => _inner.QueryComputeLocalMemorySize();

        /// <inheritdoc/>
        public int QueryComputeSharedMemorySize() => _inner.QueryComputeSharedMemorySize();

        /// <inheritdoc/>
        public uint QueryConstantBufferUse() => _inner.QueryConstantBufferUse();

        /// <inheritdoc/>
        public GpuGraphicsState QueryGraphicsState() => _inner.QueryGraphicsState();

        /// <inheritdoc/>
        public bool QueryHasConstantBufferDrawParameters() => _inner.QueryHasConstantBufferDrawParameters();

        /// <inheritdoc/>
        public bool QueryHasUnalignedStorageBuffer() => _inner.QueryHasUnalignedStorageBuffer();

        /// <inheritdoc/>
        public int QueryHostGatherBiasPrecision() => _inner.QueryHostGatherBiasPrecision();

        /// <inheritdoc/>
        public bool QueryHostReducedPrecision() => _inner.QueryHostReducedPrecision();

        /// <inheritdoc/>
        public bool QueryHostHasFrontFacingBug() => _inner.QueryHostHasFrontFacingBug();

        /// <inheritdoc/>
        public bool QueryHostHasVectorIndexingBug() => _inner.QueryHostHasVectorIndexingBug();

        /// <inheritdoc/>
        public int QueryHostStorageBufferOffsetAlignment() => _inner.QueryHostStorageBufferOffsetAlignment();

        /// <inheritdoc/>
        public int QueryHostSubgroupSize() => _inner.QueryHostSubgroupSize();

        /// <inheritdoc/>
        public bool QueryHostSupportsBgraFormat() => _inner.QueryHostSupportsBgraFormat();

        /// <inheritdoc/>
        public bool QueryHostSupportsFragmentShaderInterlock() => _inner.QueryHostSupportsFragmentShaderInterlock();

        /// <inheritdoc/>
        public bool QueryHostSupportsFragmentShaderOrderingIntel() => _inner.QueryHostSupportsFragmentShaderOrderingIntel();

        /// <inheritdoc/>
        public bool QueryHostSupportsGeometryShader() => _inner.QueryHostSupportsGeometryShader();

        /// <inheritdoc/>
        public bool QueryHostSupportsGeometryShaderPassthrough() => _inner.QueryHostSupportsGeometryShaderPassthrough();

        /// <inheritdoc/>
        public bool QueryHostSupportsImageLoadFormatted() => _inner.QueryHostSupportsImageLoadFormatted();

        /// <inheritdoc/>
        public bool QueryHostSupportsLayerVertexTessellation() => _inner.QueryHostSupportsLayerVertexTessellation();

        /// <inheritdoc/>
        public bool QueryHostSupportsNonConstantTextureOffset() => _inner.QueryHostSupportsNonConstantTextureOffset();

        /// <inheritdoc/>
        public bool QueryHostSupportsScaledVertexFormats() => _inner.QueryHostSupportsScaledVertexFormats();

        /// <inheritdoc/>
        public bool QueryHostSupportsSeparateSampler() => _inner.QueryHostSupportsSeparateSampler();

        /// <inheritdoc/>
        public bool QueryHostSupportsShaderBallot() => _inner.QueryHostSupportsShaderBallot();

        /// <inheritdoc/>
        public bool QueryHostSupportsShaderBarrierDivergence() => _inner.QueryHostSupportsShaderBarrierDivergence();

        /// <inheritdoc/>
        public bool QueryHostSupportsShaderFloat64() => _inner.QueryHostSupportsShaderFloat64();

        /// <inheritdoc/>
        public bool QueryHostSupportsShaderNonUniformIndexing() => _inner.QueryHostSupportsShaderNonUniformIndexing();

        /// <inheritdoc/>
        public bool QueryHostSupportsSnormBufferTextureFormat() => _inner.QueryHostSupportsSnormBufferTextureFormat();

        /// <inheritdoc/>
        public bool QueryHostSupportsTextureGatherOffsets() => _inner.QueryHostSupportsTextureGatherOffsets();

        /// <inheritdoc/>
        public bool QueryHostSupportsTextureShadowLod() => _inner.QueryHostSupportsTextureShadowLod();

        /// <inheritdoc/>
        public bool QueryHostSupportsTransformFeedback() => _inner.QueryHostSupportsTransformFeedback();

        /// <inheritdoc/>
        public bool QueryHostSupportsViewportIndexVertexTessellation() => _inner.QueryHostSupportsViewportIndexVertexTessellation();

        /// <inheritdoc/>
        public bool QueryHostSupportsViewportMask() => _inner.QueryHostSupportsViewportMask();

        /// <inheritdoc/>
        public bool QueryHostSupportsDepthClipControl() => _inner.QueryHostSupportsDepthClipControl();

        /// <inheritdoc/>
        public int QuerySamplerArrayLengthFromPool() => _inner.QuerySamplerArrayLengthFromPool();

        /// <inheritdoc/>
        public SamplerType QuerySamplerType(int handle, int cbufSlot = -1) => _inner.QuerySamplerType(handle, cbufSlot);

        /// <inheritdoc/>
        public int QueryTextureArrayLengthFromBuffer(int slot) => _inner.QueryTextureArrayLengthFromBuffer(slot);

        /// <inheritdoc/>
        public int QueryTextureArrayLengthFromPool() => _inner.QueryTextureArrayLengthFromPool();

        /// <inheritdoc/>
        public bool QueryTextureCoordNormalized(int handle, int cbufSlot = -1) => _inner.QueryTextureCoordNormalized(handle, cbufSlot);

        /// <inheritdoc/>
        public TextureFormat QueryTextureFormat(int handle, int cbufSlot = -1) => _inner.QueryTextureFormat(handle, cbufSlot);

        /// <inheritdoc/>
        public bool QueryTransformFeedbackEnabled() => _inner.QueryTransformFeedbackEnabled();

        /// <inheritdoc/>
        public ReadOnlySpan<byte> QueryTransformFeedbackVaryingLocations(int bufferIndex) => _inner.QueryTransformFeedbackVaryingLocations(bufferIndex);

        /// <inheritdoc/>
        public int QueryTransformFeedbackStride(int bufferIndex) => _inner.QueryTransformFeedbackStride(bufferIndex);

        /// <inheritdoc/>
        public void RegisterTexture(int handle, int cbufSlot) => _inner.RegisterTexture(handle, cbufSlot);

        /// <inheritdoc/>
        public bool IsNx2Ampere => _inner.IsNx2Ampere;
    }
}
