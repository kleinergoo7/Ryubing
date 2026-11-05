// SPDX-License-Identifier: MIT
using ARMeilleure.State;
using System.Runtime.InteropServices;

namespace ARMeilleure.Instructions
{
    static partial class SoftFallback
    {
        /// <summary>
        /// EOR3 (FEAT_SHA3): three-input exclusive OR, per byte.
        /// </summary>
        [UnmanagedCallersOnly]
        public static V128 Eor3(V128 n, V128 m, V128 a)
        {
            return n ^ m ^ a;
        }

        /// <summary>
        /// BCAX (FEAT_SHA3): <c>Vn XOR (Vm AND NOT Va)</c>, per byte.
        /// </summary>
        [UnmanagedCallersOnly]
        public static V128 Bcax(V128 n, V128 m, V128 a)
        {
            return n ^ (m & ~a);
        }

        /// <summary>
        /// RAX1 (FEAT_SHA3): <c>(Vn &lt;&lt; 1) XOR (Vm &gt;&gt; 63)</c>, per 64-bit lane.
        /// </summary>
        [UnmanagedCallersOnly]
        public static V128 Rax1(V128 n, V128 m)
        {
            return new V128(
                (ulong)((n.Extract<ulong>(0) << 1) ^ (m.Extract<ulong>(0) >> 63)),
                (ulong)((n.Extract<ulong>(1) << 1) ^ (m.Extract<ulong>(1) >> 63)));
        }

        /// <summary>
        /// XAR (FEAT_SHA3): <c>(Vn XOR Vm)</c> rotated right by <paramref name="shift"/>, per 64-bit lane.
        /// </summary>
        [UnmanagedCallersOnly]
        public static V128 Xar(V128 n, V128 m, int shift)
        {
            // The encoding only allows 0..63; a wider value would make the rotate
            // counts wrap and silently produce a wrong result.
            if ((uint)shift > 63u)
            {
                throw new System.ArgumentOutOfRangeException(nameof(shift), shift, "XAR rotate must be 0..63.");
            }

            ulong x0 = (ulong)(n.Extract<ulong>(0) ^ m.Extract<ulong>(0));
            ulong x1 = (ulong)(n.Extract<ulong>(1) ^ m.Extract<ulong>(1));

            if (shift != 0)
            {
                x0 = (x0 >> shift) | (x0 << (64 - shift));
                x1 = (x1 >> shift) | (x1 << (64 - shift));
            }

            return new V128(x0, x1);
        }
    }
}
