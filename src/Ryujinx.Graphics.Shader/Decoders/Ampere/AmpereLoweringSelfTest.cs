using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Ryujinx.Graphics.Shader.Translation;

namespace Ryujinx.Graphics.Shader.Decoders.Ampere
{
    /// <summary>
    /// Assert-based checks for the offline Ampere lowering core.
    /// </summary>
    /// <remarks>
    /// The shader project has no test project of its own, so the checks travel with the code
    /// and are run by <c>Ryujinx.ShaderTools --ampere-self-test</c>.
    /// </remarks>
    public static class AmpereLoweringSelfTest
    {
        public static void Run()
        {
            CheckLayout();
            CheckLowering();
            CheckReturns();
            CheckCapture();
            CheckImageAndAddressMap();
            CheckDecoder();
            CheckRuntimeAdapter();
        }

        private static void CheckLayout()
        {
            uint[] expected = [8, 16, 24, 40, 48, 56, 72, 80, 88];

            for (uint index = 0; index < expected.Length; index++)
            {
                uint address = AmpereLayout.AddressOf(index);

                Assert(address == expected[index], $"layout offset {index}");
                Assert((address & 31) != 0, $"layout offset {index} collides with a scheduling word");
            }
        }

        private static void CheckLowering()
        {
            AmpereInstruction[] code =
            [
                Encode(0x882, dest: 0, W1: 0x1234),
                Encode(0x424, dest: 1, W1: 0x5678, W2: 0x078e00ff, sourceA: 255, sourceC: 255),
                Exit(),
            ];

            AmpereLoweredProgram lowered = AmpereLowering.LowerToMaxwell(code);

            Assert(lowered.Success, $"simple lowering failed: {lowered.Error}");
            Assert(lowered.Instructions.Count == 3, "simple lowering instruction count");
            Assert(lowered.SourceSize == code.Length * AmpereInstruction.Size, "simple lowering source size");
            Assert(lowered.Instructions[0] == (0x0100000000000000UL | 0x70000UL | 216UL | 0xF000UL | (0x1234UL << 20)),
                "uniform immediate move");
            Assert(((lowered.Instructions[1] >> 20) & 0xffffffffUL) == 0x5678, "IMAD.MOV immediate");
            Assert(lowered.Instructions[2] == (0xe30000000000000fUL | (7UL << 16)), "EXIT");
        }

        private static void CheckReturns()
        {
            foreach (uint link in new uint[] { 0, 55 })
            {
                AmpereInstruction[] code =
                [
                    new(0x00007802U | (link << 16), 0x20, 0x00000f00, 0),
                    new(0x00007944, 0x10, 0x03c00000, 0),
                    new(0x0000794d, 0, 0x03800000, 0),
                    new(0x00007950U | (link << 24), 0xffffffc0, 0x03c3ffff, 0),
                ];

                AmpereLoweredProgram lowered = AmpereLowering.LowerToMaxwell(code);

                Assert(lowered.Success, $"return lowering failed: {lowered.Error}");
                Assert(lowered.SourceSize == code.Length * AmpereInstruction.Size, "return source size");
                Assert(lowered.Instructions[^1] == 0xe32000000007000fUL, "return does not write the link register");
            }

            {
                AmpereInstruction[] code =
                [
                    new(0x00007802, 0x20, 0x00000f00, 0),
                    new(0x00007944, 0x10, 0x03c00000, 0),
                    new(0x0000794d, 0, 0x03800000, 0),
                    new(0x00017202, 0, 0x00000f00, 0),
                    new(0x01007950, 0xffffffb0, 0x03c3ffff, 0),
                ];

                Assert(AmpereLowering.LowerToMaxwell(code).Success, "return can read a copy of the R0 link");
            }

            {
                AmpereInstruction[] code =
                [
                    new(0x00007802, 0x20, 0x00000f00, 0),
                    new(0x00007944, 0x10, 0x03c00000, 0),
                    new(0x0000794d, 0, 0x03800000, 0),
                    new(0x00007802, 0, 0x00000f00, 0),
                    new(0x00007950, 0xffffffb0, 0x03c3ffff, 0),
                ];

                AmpereLoweredProgram lowered = AmpereLowering.LowerToMaxwell(code);

                Assert(!lowered.Success, "callee must not overwrite the link register");
                Assert(lowered.Error == "native callee overwrites link register", "callee link error message");
                Assert(lowered.ErrorOffset == 0x30, "callee link error offset");
            }
        }

        private static void CheckCapture()
        {
            AmpereProgramCapture capture = new();

            Assert(capture.Observe(Exit(), 0) == AmpereProgramCapture.Result.Complete, "EXIT ends the program");
        }

        private static void CheckImageAndAddressMap()
        {
            AmpereInstruction[] code =
            [
                Encode(0x882, dest: 0, W1: 0x1234),
                Exit(),
            ];

            AmpereLoweredProgram lowered = AmpereLowering.LowerToMaxwell(code);

            Assert(lowered.Success, "image lowering failed");

            byte[] image = NativeShaderImage.Build([], lowered);
            ulong[] words = NativeShaderImage.ReadCodeWords(image);

            Assert(image.Length >= AmpereLayout.HeaderSize, "image header");
            Assert(words[0] == 0, "first scheduling word");
            Assert(words[1] == lowered.Instructions[0], "first instruction slot");
            Assert(words[2] == lowered.Instructions[1], "second instruction slot");
            Assert(NativeAddressMap.Has(words), "address map trailer");
            Assert(NativeAddressMap.FindAddress(words, 0) == AmpereLayout.AddressOf(0), "native PC 0 mapping");
            Assert(NativeAddressMap.FindAddress(words, 16) == AmpereLayout.AddressOf(1), "native PC 16 mapping");
            Assert(NativeAddressMap.FindAddress(words, 4) == null, "unaligned native PC rejected");
        }

        private static void CheckDecoder()
        {
            AmpereInstruction[] code =
            [
                Encode(0x882, dest: 0, W1: 0x10),
                Encode(0x882, dest: 1, W1: 0x20),
                Encode(0x882, dest: 2, W1: 0x30),
                Encode(0x424, dest: 3, W1: 0x40, W2: 0x078e00ff, sourceA: 255, sourceC: 255),
                Exit(),
            ];

            AmpereLoweredProgram lowered = AmpereLowering.LowerToMaxwell(code);

            Assert(lowered.Success, "decoder lowering failed");

            byte[] image = NativeShaderImage.Build([], lowered, hasHeader: false);
            ShaderDefinitions definitions = new(ShaderStage.Compute, 1, 1, 1);
            DecodedProgram program = Decoder.Decode(definitions, new ImageGpuAccessor(image), 0);

            int count = 0;
            InstName lastName = default;
            List<InstName> names = [];

            foreach (DecodedFunction function in program)
            {
                foreach (Block block in function.Blocks)
                {
                    count += block.OpCodes.Count;
                    lastName = block.OpCodes[^1].Name;

                    foreach (InstOp op in block.OpCodes)
                    {
                        names.Add(op.Name);
                    }
                }
            }

            Assert(count == lowered.Instructions.Count, "Ryujing decodes every lowered word past the scheduling pads");
            Assert(lastName == InstName.Exit, "decoded program ends at EXIT");
            Assert(names.Count == 5, "decoded op count");

            for (int index = 0; index < 4; index++)
            {
                Assert(names[index] == InstName.Mov32i, $"lowered word {index} decodes as {names[index]}");
            }
        }

        private static void CheckRuntimeAdapter()
        {
            AmpereInstruction[] code =
            [
                Encode(0x882, dest: 0, W1: 0x10),
                Exit(),
            ];

            AmpereLoweredProgram lowered = AmpereLowering.LowerToMaxwell(code);

            Assert(lowered.Success, "runtime adapter lowering failed");

            byte[] native = new byte[code.Length * AmpereInstruction.Size];

            for (int index = 0; index < code.Length; index++)
            {
                BitConverter.TryWriteBytes(native.AsSpan(index * AmpereInstruction.Size), code[index].W0);
                BitConverter.TryWriteBytes(native.AsSpan(index * AmpereInstruction.Size + 4), code[index].W1);
                BitConverter.TryWriteBytes(native.AsSpan(index * AmpereInstruction.Size + 8), code[index].W2);
                BitConverter.TryWriteBytes(native.AsSpan(index * AmpereInstruction.Size + 12), code[index].W3);
            }

            // Compute has no header: the program starts at the shader address.
            TranslationOptions computeOptions = new(TargetLanguage.Glsl, TargetApi.OpenGL, TranslationFlags.Compute);
            TranslatorContext computeContext = Translator.CreateContext(
                0,
                new NativeGpuAccessor(native),
                computeOptions);

            Assert(computeContext.Size == native.Length, "runtime adapter compute size");
            Assert(computeContext.Stage == ShaderStage.Compute, "runtime adapter compute stage");

            // Graphics keeps the guest header ahead of the lowered code.
            byte[] graphics = new byte[AmpereLayout.HeaderSize + native.Length];

            native.CopyTo(graphics, AmpereLayout.HeaderSize);

            TranslationOptions graphicsOptions = new(TargetLanguage.Glsl, TargetApi.OpenGL, TranslationFlags.None);
            TranslatorContext graphicsContext = Translator.CreateContext(
                0,
                new NativeGpuAccessor(graphics),
                graphicsOptions);

            Assert(graphicsContext.Size == graphics.Length, "runtime adapter graphics size");
            Assert(graphicsContext.Stage == ShaderStage.Vertex, "runtime adapter graphics stage");
        }

        private sealed class NativeGpuAccessor : IGpuAccessor
        {
            private readonly byte[] _data;

            public NativeGpuAccessor(byte[] data)
            {
                _data = data;
            }

            public bool IsNx2Ampere => true;

            public ReadOnlySpan<ulong> GetCode(ulong address, int minimumSize)
            {
                return MemoryMarshal.Cast<byte, ulong>(_data.AsSpan((int)address));
            }

            public SetBindingPair CreateConstantBufferBinding(int index) => new(0, index + 1);

            public SetBindingPair CreateImageBinding(int count, bool isBuffer) => new(3, 0);

            public SetBindingPair CreateStorageBufferBinding(int index) => new(1, index);

            public SetBindingPair CreateTextureBinding(int count, bool isBuffer) => new(2, 0);

            public int QuerySamplerArrayLengthFromPool() => 32;

            public int QueryTextureArrayLengthFromBuffer(int slot) => 32;

            public int QueryTextureArrayLengthFromPool() => 32;
        }

        private sealed class ImageGpuAccessor : IGpuAccessor
        {
            private readonly byte[] _image;

            public ImageGpuAccessor(byte[] image)
            {
                _image = image;
            }

            public ReadOnlySpan<ulong> GetCode(ulong address, int minimumSize)
            {
                return MemoryMarshal.Cast<byte, ulong>(_image.AsSpan((int)address));
            }

            public SetBindingPair CreateConstantBufferBinding(int index) => new(0, index + 1);

            public SetBindingPair CreateImageBinding(int count, bool isBuffer) => new(3, 0);

            public SetBindingPair CreateStorageBufferBinding(int index) => new(1, index);

            public SetBindingPair CreateTextureBinding(int count, bool isBuffer) => new(2, 0);

            public int QuerySamplerArrayLengthFromPool() => 32;

            public int QueryTextureArrayLengthFromBuffer(int slot) => 32;

            public int QueryTextureArrayLengthFromPool() => 32;
        }

        private static AmpereInstruction Encode(
            uint opcode,
            uint dest,
            uint W1 = 0,
            uint W2 = 0,
            uint sourceA = 0,
            uint sourceC = 0)
        {
            uint W0 = opcode | (7U << 12) | (dest << 16) | (sourceA << 24);

            return new AmpereInstruction(W0, W1, W2, 0);
        }

        private static AmpereInstruction Exit()
        {
            return new AmpereInstruction(0x94dU | (7U << 12), 0, 7U << 23, 0);
        }

        private static void Assert(bool condition, string what)
        {
            if (!condition)
            {
                throw new InvalidOperationException($"Ampere lowering self-test failed: {what}.");
            }
        }
    }
}
