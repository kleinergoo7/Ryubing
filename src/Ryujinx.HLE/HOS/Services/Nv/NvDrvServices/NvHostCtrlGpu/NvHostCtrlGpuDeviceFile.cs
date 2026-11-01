using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Kernel.Threading;
using Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvHostCtrlGpu.Types;
using Ryujinx.Horizon.Common;
using Ryujinx.Memory;
using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvHostCtrlGpu
{
    class NvHostCtrlGpuDeviceFile : NvDeviceFile
    {
        private static readonly Stopwatch _pTimer = new();
        private static readonly double _ticksToNs = (1.0 / Stopwatch.Frequency) * 1_000_000_000;

        public static bool Nx2Driver { get; private set; }

        private bool _nx2Caller;

        private readonly KEvent _errorEvent;
        private readonly KEvent _unknownEvent;

        public NvHostCtrlGpuDeviceFile(ServiceCtx context, IVirtualMemoryManager memory, ulong owner) : base(context, owner)
        {
            _errorEvent = new KEvent(context.Device.System.KernelContext);
            _unknownEvent = new KEvent(context.Device.System.KernelContext);
        }

        static NvHostCtrlGpuDeviceFile()
        {
            _pTimer.Start();
        }

        public override NvInternalResult Ioctl(NvIoctl command, Span<byte> arguments)
        {
            NvInternalResult result = NvInternalResult.NotImplemented;

            if (command.Type == NvIoctl.NvGpuMagic)
            {
                switch (command.Number)
                {
                    case 0x01:
                        result = CallIoctlMethod<ZcullGetCtxSizeArguments>(ZcullGetCtxSize, arguments);
                        break;
                    case 0x02:
                        result = CallIoctlMethod<ZcullGetInfoArguments>(ZcullGetInfo, arguments);
                        break;
                    case 0x03:
                        result = CallIoctlMethod<ZbcSetTableArguments>(ZbcSetTable, arguments);
                        break;
                    case 0x05:
                        result = CallIoctlMethod<GetCharacteristicsArguments>(GetCharacteristics, arguments);
                        break;
                    case 0x06:
                        result = CallIoctlMethod<GetTpcMasksArguments>(GetTpcMasks, arguments);
                        break;
                    case 0x12:
                        result = CallIoctlMethod<NumVsmsArguments>(NumVsms, arguments);
                        break;
                    case 0x13:
                        result = CallIoctlMethod<VsmsMappingArguments>(VsmsMapping, arguments);
                        break;
                    case 0x14:
                        result = CallIoctlMethod<GetActiveSlotMaskArguments>(GetActiveSlotMask, arguments);
                        break;
                    case 0x1c:
                        result = CallIoctlMethod<GetGpuTimeArguments>(GetGpuTime, arguments);
                        break;
                }
            }

            return result;
        }

        public override NvInternalResult Ioctl3(NvIoctl command, Span<byte> arguments, Span<byte> inlineOutBuffer)
        {
            NvInternalResult result = NvInternalResult.NotImplemented;

            if (command.Type == NvIoctl.NvGpuMagic)
            {
                switch (command.Number)
                {
                    case 0x05:
                        result = CallIoctlMethod<GetCharacteristicsArguments, GpuCharacteristics>(GetCharacteristics, arguments, inlineOutBuffer);

                        if (_nx2Caller && inlineOutBuffer.Length >= 0xb8)
                        {
                            // NvRm sizes each TSG's subcontext (VEID) allocator from the u32 at +0xb4
                            // when flags bits 8 and 22 are set; otherwise a second queue's
                            // NvRmGpuTSGSubcontextCreate fails (0x11) and its NVN command buffer stays unallocated.
                            BinaryPrimitives.WriteUInt32LittleEndian(inlineOutBuffer[0xb4..], 64);
                        }

                        break;
                    case 0x06:
                        result = CallIoctlMethod<GetTpcMasksArguments, int>(GetTpcMasks, arguments, inlineOutBuffer);
                        break;
                    case 0x12:
                        result = CallIoctlMethod<NumVsmsArguments>(NumVsms, arguments);
                        break;
                    case 0x13:
                        result = CallIoctlMethod<VsmsMappingArguments>(VsmsMapping, arguments);
                        break;
                    case 0x1F:
                        if (_nx2Caller)
                        {
                            result = GetEngineInfo(arguments, inlineOutBuffer);
                        }
                        break;
                }
            }

            if (_nx2Caller && result == NvInternalResult.NotImplemented)
            {
                Logger.Warning?.Print(LogClass.ServiceNv, $"Switch 2 ctrl-gpu ioctl type={command.Type} nr=0x{command.Number:X2} stubbed");

                arguments.Clear();
                inlineOutBuffer.Clear();

                return NvInternalResult.Success;
            }

            return result;
        }

        public override NvInternalResult QueryEvent(out int eventHandle, uint eventId)
        {
            // TODO: accurately represent and implement those events.
            KEvent targetEvent = null;

            switch (eventId)
            {
                case 0x1:
                    targetEvent = _errorEvent;
                    break;
                case 0x2:
                    targetEvent = _unknownEvent;
                    break;
            }

            if (targetEvent != null)
            {
                if (Context.Process.HandleTable.GenerateHandle(targetEvent.ReadableEvent, out eventHandle) != Result.Success)
                {
                    throw new InvalidOperationException("Out of handles!");
                }
            }
            else
            {
                eventHandle = 0;

                return NvInternalResult.InvalidInput;
            }

            return NvInternalResult.Success;
        }

        public override void Close() { }

        private NvInternalResult ZcullGetCtxSize(ref ZcullGetCtxSizeArguments arguments)
        {
            arguments.Size = 1;

            return NvInternalResult.Success;
        }

        private NvInternalResult ZcullGetInfo(ref ZcullGetInfoArguments arguments)
        {
#pragma warning disable IDE0055 // Disable formatting
            arguments.WidthAlignPixels           = 0x20;
            arguments.HeightAlignPixels          = 0x20;
            arguments.PixelSquaresByAliquots     = 0x400;
            arguments.AliquotTotal               = 0x800;
            arguments.RegionByteMultiplier       = 0x20;
            arguments.RegionHeaderSize           = 0x20;
            arguments.SubregionHeaderSize        = 0xc0;
            arguments.SubregionWidthAlignPixels  = 0x20;
            arguments.SubregionHeightAlignPixels = 0x40;
            arguments.SubregionCount             = 0x10;
#pragma warning restore IDE0055

            return NvInternalResult.Success;
        }

        private NvInternalResult ZbcSetTable(ref ZbcSetTableArguments arguments)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceNv);

            return NvInternalResult.Success;
        }

        private NvInternalResult GetCharacteristics(ref GetCharacteristicsArguments arguments)
        {
            return GetCharacteristics(ref arguments, ref arguments.Characteristics);
        }

        private NvInternalResult GetCharacteristics(ref GetCharacteristicsArguments arguments, ref GpuCharacteristics characteristics)
        {
            bool nx2 = arguments.Header.BufferSize >= 0xD0;

            _nx2Caller |= nx2;
            Nx2Driver |= nx2;

            if (nx2)
            {
                Context.Device.Gpu.IsNx2Ampere = true;
            }

            arguments.Header.BufferSize = nx2 ? 0xD0 : 0xa0;

#pragma warning disable IDE0055 // Disable formatting
            characteristics.Arch                   = 0x120;
            characteristics.Impl                   = 0xb;
            characteristics.Rev                    = 0xa1;
            characteristics.NumGpc                 = 0x1;
            characteristics.L2CacheSize            = 0x40000;
            characteristics.OnBoardVideoMemorySize = 0x0;
            characteristics.NumTpcPerGpc           = 0x2;
            characteristics.BusType                = 0x20;
            characteristics.BigPageSize            = 0x20000;
            characteristics.CompressionPageSize    = 0x20000;
            characteristics.PdeCoverageBitCount    = 0x1b;
            characteristics.AvailableBigPageSizes  = 0x30000;
            characteristics.GpcMask                = 0x1;
            characteristics.SmArchSmVersion        = 0x503;
            characteristics.SmArchSpaVersion       = 0x503;
            characteristics.SmArchWarpCount        = 0x80;
            characteristics.GpuVaBitCount          = 0x28;
            characteristics.Reserved               = 0x0;
            characteristics.Flags                  = 0x55;
            characteristics.TwodClass              = 0x902d;
            characteristics.ThreedClass            = 0xb197;
            characteristics.ComputeClass           = 0xb1c0;
            characteristics.GpfifoClass            = 0xb06f;
            characteristics.InlineToMemoryClass    = 0xa140;
            characteristics.DmaCopyClass           = 0xb0b5;
            characteristics.MaxFbpsCount           = 0x1;
            characteristics.FbpEnMask              = 0x0;
            characteristics.MaxLtcPerFbp           = 0x2;
            characteristics.MaxLtsPerLtc           = 0x1;
            characteristics.MaxTexPerTpc           = 0x0;
            characteristics.MaxGpcCount            = 0x1;
            characteristics.RopL2EnMask0           = 0x21d70;
            characteristics.RopL2EnMask1           = 0x0;
            characteristics.ChipName               = 0x6230326d67;
            characteristics.GrCompbitStoreBaseHw   = 0x0;

            if (nx2)
            {
                // Switch 2 NvRm rejects Maxwell; report an Ampere chip (0x17E).
                characteristics.Arch            = 0x170;
                characteristics.Impl            = 0xE;
                characteristics.NumTpcPerGpc    = 0x6;
                characteristics.SmArchSmVersion = 0x808;
                characteristics.SmArchSpaVersion = 0x808;
                characteristics.ThreedClass     = 0xC797;
                characteristics.ComputeClass    = 0xC7C0;
                characteristics.GpfifoClass     = 0xC56F;
                characteristics.DmaCopyClass    = 0xC7B5;
                characteristics.ChipName        = 0x6631306167;
                characteristics.Flags          |= 0x100;          // SUPPORT_TSG
                characteristics.Flags          |= 1L << 22;      // SUPPORT_TSG_SUBCONTEXTS: NvRm sizes each TSG's VEID allocator from the +0xb4 u32
                characteristics.Flags          |= 1L << 34;      // SUPPORT_MAPPING_MODIFY: NvRm switches a compressible pool's mapping to the texture's kind
                characteristics.Flags          |= 1L << 26;      // SUPPORT_SYNCPOINT_ADDRESS (shim)

                // Nine s16 interface versions at 0x70..0x80.
                ref byte raw = ref Unsafe.As<GpuCharacteristics, byte>(ref characteristics);

                for (int offset = 0x70; offset <= 0x80; offset += 2)
                {
                    BinaryPrimitives.WriteInt16LittleEndian(MemoryMarshal.CreateSpan(ref Unsafe.Add(ref raw, offset), 2), 0x40);
                }
            }
#pragma warning restore IDE0055

            arguments.Characteristics = characteristics;

            return NvInternalResult.Success;
        }

        private static NvInternalResult GetEngineInfo(Span<byte> arguments, Span<byte> inlineOutBuffer)
        {
            uint bufSize = arguments.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(arguments) : 0;
            const uint Needed = 2 * 16;

            if (bufSize >= Needed && inlineOutBuffer.Length >= Needed)
            {
                Span<byte> items = inlineOutBuffer[..(int)Needed];
                items.Clear();

                BinaryPrimitives.WriteUInt32LittleEndian(items[0..], 0);  // GR
                BinaryPrimitives.WriteUInt32LittleEndian(items[4..], 0);
                BinaryPrimitives.WriteInt32LittleEndian(items[8..], 0);
                BinaryPrimitives.WriteUInt32LittleEndian(items[12..], 0);

                BinaryPrimitives.WriteUInt32LittleEndian(items[16..], 2); // async copy
                BinaryPrimitives.WriteUInt32LittleEndian(items[20..], 0);
                BinaryPrimitives.WriteInt32LittleEndian(items[24..], 1);
                BinaryPrimitives.WriteUInt32LittleEndian(items[28..], 0);
            }

            arguments.Clear();

            if (arguments.Length >= 4)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(arguments, Needed);
            }

            return NvInternalResult.Success;
        }

        private NvInternalResult GetTpcMasks(ref GetTpcMasksArguments arguments)
        {
            return GetTpcMasks(ref arguments, ref arguments.TpcMask);
        }

        private NvInternalResult GetTpcMasks(ref GetTpcMasksArguments arguments, ref int tpcMask)
        {
            if (arguments.MaskBufferSize != 0)
            {
                tpcMask = 3;
                arguments.TpcMask = tpcMask;
            }

            return NvInternalResult.Success;
        }

        private NvInternalResult NumVsms(ref NumVsmsArguments arguments)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceNv);

            arguments.NumVsms = 2;

            return NvInternalResult.Success;
        }

        private NvInternalResult VsmsMapping(ref VsmsMappingArguments arguments)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceNv);

            arguments.Sm0GpcIndex = 0;
            arguments.Sm0TpcIndex = 0;
            arguments.Sm1GpcIndex = 0;
            arguments.Sm1TpcIndex = 1;

            return NvInternalResult.Success;
        }

        private NvInternalResult GetActiveSlotMask(ref GetActiveSlotMaskArguments arguments)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceNv);

            arguments.Slot = 0x07;
            arguments.Mask = 0x01;

            return NvInternalResult.Success;
        }

        private NvInternalResult GetGpuTime(ref GetGpuTimeArguments arguments)
        {
            arguments.Timestamp = GetPTimerNanoSeconds();

            return NvInternalResult.Success;
        }

        private static ulong GetPTimerNanoSeconds()
        {
            double ticks = _pTimer.ElapsedTicks;

            return (ulong)(ticks * _ticksToNs) & 0xff_ffff_ffff_ffff;
        }
    }
}
