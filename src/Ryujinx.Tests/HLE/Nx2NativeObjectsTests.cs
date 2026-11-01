using NUnit.Framework;
using Ryujinx.HLE.HOS.Services.Am.AppletAE.AllSystemAppletProxiesService.SystemAppletProxy;
using System;

namespace Ryujinx.Tests.HLE
{
    public class Nx2NativeObjectsTests
    {
        private static uint[] Preference(uint width, uint height, uint blockHeightLog2, uint pitchGobs)
        {
            uint[] preference = new uint[16];

            preference[2] = ((height - 1) << 16) | (width - 1);
            preference[4] = blockHeightLog2;
            preference[5] = pitchGobs;

            return preference;
        }

        [Test]
        public void Layout2560x1440BlockHeight7()
        {
            // 2560x1440 needs (2560 * 4 + 63) / 64 = 160 GOBs per row; 160 fits, so the
            // preference pitch wins: 160 * 64 = 10240, 128 block rows, 10240 * 1536 bytes.
            (uint width, uint height, ulong pitch, uint blockHeightLog2, ulong size) =
                Nx2NativeObjects.Layout(Preference(2560, 1440, 7, 160));

            Assert.That(width, Is.EqualTo(2560u));
            Assert.That(height, Is.EqualTo(1440u));
            Assert.That(pitch, Is.EqualTo(10240uL));
            Assert.That(blockHeightLog2, Is.EqualTo(7u));
            Assert.That(size, Is.EqualTo(15728640uL));
        }

        [Test]
        public void LayoutFallsBackWhenPitchTooSmall()
        {
            // 10 GOBs cannot hold a 2560-pixel row, so the layout aligns width * 4 up to 64
            // and resets the block height to 7.
            (uint _, uint _, ulong pitch, uint blockHeightLog2, ulong _) =
                Nx2NativeObjects.Layout(Preference(2560, 1440, 3, 10));

            Assert.That(pitch, Is.EqualTo(10240uL));
            Assert.That(blockHeightLog2, Is.EqualTo(7u));
        }

        [Test]
        public void LayoutRejectsShortPreference()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => Nx2NativeObjects.Layout(new uint[4]));
            Assert.Throws<ArgumentOutOfRangeException>(() => Nx2NativeObjects.Layout(null));
        }

        [Test]
        public void SwizzleOffsetCorners()
        {
            // Table word 0 is 0x12100200: position 0 reads byte 0x00, position 511 (last byte
            // of the 64x8 GOB) reads word 7 byte 0x1f. Neither needs the native-tiling tweak.
            Assert.That(Nx2NativeObjects.SwizzleOffset(0, 0), Is.EqualTo(0u));
            Assert.That(Nx2NativeObjects.SwizzleOffset(63, 7), Is.EqualTo(511u));
            Assert.That(Nx2NativeObjects.SwizzleOffset(1, 0), Is.EqualTo(1u));
        }

        [Test]
        public void UnswizzleOneGobRowIsAPermutation()
        {
            // A single 64x8 GOB row at block height 0: every destination byte comes from
            // exactly one source byte, and the first pixel is fixed by the table.
            const int width = 64;
            const int height = 8;

            byte[] source = new byte[width * height * 4];
            byte[] linear = new byte[width * height * 4];

            for (int i = 0; i < source.Length; i++)
            {
                source[i] = (byte)(i % 251);
            }

            Nx2NativeObjects.Unswizzle(source, linear, width, height, 64, 3);

            Assert.That(linear[0], Is.EqualTo(source[0]));
            Assert.That(linear[1], Is.EqualTo(source[1]));
            Assert.That(linear[2], Is.EqualTo(source[2]));
            Assert.That(linear[3], Is.EqualTo(source[3]));

            long sourceSum = 0;
            long linearSum = 0;

            for (int i = 0; i < source.Length; i++)
            {
                sourceSum += source[i];
                linearSum += linear[i];
            }

            Assert.That(linearSum, Is.EqualTo(sourceSum));
        }
    }
}
