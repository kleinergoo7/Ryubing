using CommandLine;
using Ryujinx.Graphics.Shader;
using Ryujinx.Graphics.Shader.Decoders.Ampere;
using Ryujinx.Graphics.Shader.Translation;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Ryujinx.ShaderTools
{
    class Program
    {
        private class GpuAccessor : IGpuAccessor
        {
            private const int DefaultArrayLength = 32;

            private readonly byte[] _data;

            private int _texturesCount;
            private int _imagesCount;

            public GpuAccessor(byte[] data)
            {
                _data = data;
                _texturesCount = 0;
                _imagesCount = 0;
            }

            public SetBindingPair CreateConstantBufferBinding(int index)
            {
                return new SetBindingPair(0, index + 1);
            }

            public SetBindingPair CreateImageBinding(int count, bool isBuffer)
            {
                int binding = _imagesCount;

                _imagesCount += count;

                return new SetBindingPair(3, binding);
            }

            public SetBindingPair CreateStorageBufferBinding(int index)
            {
                return new SetBindingPair(1, index);
            }

            public SetBindingPair CreateTextureBinding(int count, bool isBuffer)
            {
                int binding = _texturesCount;

                _texturesCount += count;

                return new SetBindingPair(2, binding);
            }

            public ReadOnlySpan<ulong> GetCode(ulong address, int minimumSize)
            {
                return MemoryMarshal.Cast<byte, ulong>(new ReadOnlySpan<byte>(_data)[(int)address..]);
            }

            public int QuerySamplerArrayLengthFromPool()
            {
                return DefaultArrayLength;
            }

            public int QueryTextureArrayLengthFromBuffer(int slot)
            {
                return DefaultArrayLength;
            }

            public int QueryTextureArrayLengthFromPool()
            {
                return DefaultArrayLength;
            }
        }

        private class Options
        {
            [Option("compute", Required = false, Default = false, HelpText = "Indicate that the shader is a compute shader.")]
            public bool Compute { get; set; }

            [Option("vertex-as-compute", Required = false, Default = false, HelpText = "Indicate that the shader is a vertex shader and should be converted to compute.")]
            public bool VertexAsCompute { get; set; }

            [Option("vertex-passthrough", Required = false, Default = false, HelpText = "Indicate that the shader is a vertex passthrough shader for compute output.")]
            public bool VertexPassthrough { get; set; }

            [Option("ampere", Required = false, Default = false, HelpText = "Lower a native Ampere (SM86) instruction stream with the offline lowering core.")]
            public bool Ampere { get; set; }

            [Option("ampere-self-test", Required = false, Default = false, HelpText = "Run the offline Ampere lowering self-test.")]
            public bool AmpereSelfTest { get; set; }

            [Option("ampere-header", Required = false, Default = false, HelpText = "The Ampere input includes the 0x50-byte graphics shader header.")]
            public bool AmpereHeader { get; set; }

            [Option("ampere-image", Required = false, Default = false, HelpText = "Write the lowered Maxwell shader image instead of translating it.")]
            public bool AmpereImage { get; set; }

            [Option("target-language", Required = false, Default = TargetLanguage.Glsl, HelpText = "Indicate the target shader language to use.")]
            public TargetLanguage TargetLanguage { get; set; }

            [Option("target-api", Required = false, Default = TargetApi.OpenGL, HelpText = "Indicate the target graphics api to use.")]
            public TargetApi TargetApi { get; set; }

            [Value(0, MetaName = "input", HelpText = "Binary Maxwell shader input path.", Required = false)]
            public string InputPath { get; set; }

            [Value(1, MetaName = "output", HelpText = "Decompiled shader output path.", Required = false)]
            public string OutputPath { get; set; }
        }

        static void HandleArguments(Options options)
        {
            if (options.AmpereSelfTest)
            {
                AmpereLoweringSelfTest.Run();

                Console.Error.WriteLine("Ampere lowering self-test passed.");

                return;
            }

            if (options.InputPath == null)
            {
                Console.Error.WriteLine("Missing input path.");

                return;
            }

            if (options.Ampere)
            {
                HandleAmpere(options);
                return;
            }

            byte[] data = File.ReadAllBytes(options.InputPath);

            Translate(data, options);
        }

        private static void HandleAmpere(Options options)
        {
            byte[] data = File.ReadAllBytes(options.InputPath);

            int inputHeaderSize = options.AmpereHeader ? AmpereLayout.HeaderSize : 0;
            ReadOnlySpan<byte> code = data.AsSpan(inputHeaderSize);

            if (code.Length == 0 || code.Length % AmpereInstruction.Size != 0)
            {
                Console.Error.WriteLine($"Ampere input is {code.Length} bytes, not a non-empty multiple of {AmpereInstruction.Size}.");

                return;
            }

            AmpereInstruction[] instructions = NativeShaderImage.ReadInstructions(code);
            AmpereLoweredProgram lowered = AmpereLowering.LowerToMaxwell(instructions);

            if (!lowered.Success)
            {
                Console.Error.WriteLine($"Lowering failed at 0x{lowered.ErrorOffset:X}: {lowered.Error}");

                return;
            }

            Console.Error.WriteLine(
                $"Lowered {instructions.Length} native instructions to {lowered.Instructions.Count} Maxwell instructions.");

            for (int index = 0; index < lowered.Instructions.Count; index++)
            {
                uint address = AmpereLayout.AddressOf((uint)index);

                Console.Error.WriteLine($"{inputHeaderSize + address,8:X8}: {lowered.Instructions[index]:X16}");
            }

            byte[] image = NativeShaderImage.Build(data, lowered, options.AmpereHeader);

            if (options.AmpereImage)
            {
                if (options.OutputPath == null)
                {
                    Console.Error.WriteLine("--ampere-image needs an output path.");
                }
                else
                {
                    File.WriteAllBytes(options.OutputPath, image);
                }

                return;
            }

            try
            {
                Translate(image, options);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Translation of the lowered image failed: {exception.Message}");
            }
        }

        private static void Translate(byte[] data, Options options)
        {
            TranslationFlags flags = TranslationFlags.DebugMode;

            if (options.Compute)
            {
                flags |= TranslationFlags.Compute;
            }

            TranslationOptions translationOptions = new(options.TargetLanguage, options.TargetApi, flags);
            TranslatorContext translatorContext = Translator.CreateContext(0, new GpuAccessor(data), translationOptions);

            ShaderProgram program;

            if (options.VertexPassthrough)
            {
                program = translatorContext.GenerateVertexPassthroughForCompute();
            }
            else
            {
                program = translatorContext.Translate(options.VertexAsCompute);
            }

            if (options.OutputPath == null)
            {
                if (program.BinaryCode != null)
                {
                    using Stream outputStream = Console.OpenStandardOutput();

                    outputStream.Write(program.BinaryCode);
                }
                else
                {
                    Console.WriteLine(program.Code);
                }
            }
            else
            {
                if (program.BinaryCode != null)
                {
                    File.WriteAllBytes(options.OutputPath, program.BinaryCode);
                }
                else
                {
                    File.WriteAllText(options.OutputPath, program.Code);
                }
            }
        }

        static void Main(string[] args)
        {
            Parser.Default.ParseArguments<Options>(args)
                .WithParsed(options => HandleArguments(options))
                .WithNotParsed(errors => errors.Output());
        }
    }
}
