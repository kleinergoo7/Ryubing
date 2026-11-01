using ARMeilleure.Decoders;
using ARMeilleure.IntermediateRepresentation;
using ARMeilleure.Translation;
using static ARMeilleure.Instructions.InstEmitHelper;
using static ARMeilleure.Instructions.InstEmitMemoryExHelper;
using static ARMeilleure.Instructions.InstEmitMemoryHelper;
using static ARMeilleure.IntermediateRepresentation.Operand.Factory;

namespace ARMeilleure.Instructions
{
    static partial class InstEmit
    {
        // ARMv8.1 LSE atomics, implemented as a compare-and-swap retry loop.
        public static void Atomic(ArmEmitterContext context)
        {
            OpCodeAtomic op = (OpCodeAtomic)context.CurrOp;

            bool is64 = op.Size == 3;
            OperandType type = is64 ? OperandType.I64 : OperandType.I32;

            Operand address = context.Copy(GetIntOrSP(context, op.Rn));
            Operand physAddr = EmitPtPointerLoad(context, address, default, write: true, op.Size);

            Operand oldLocal = context.AllocateLocal(type);
            context.Copy(oldLocal, EmitLoad(context, physAddr, op.Size));

            Operand src = GetIntOrZR(context, op.Rs);

            if (!is64)
            {
                src = context.ConvertI64ToI32(src);
            }

            Operand retry = Label();

            context.MemoryBarrier();
            context.MarkLabel(retry);

            Operand old = context.Copy(oldLocal);

            Operand value = op.Op switch
            {
                0 => context.Add(old, src),
                1 => context.BitwiseAnd(old, context.BitwiseNot(src)),
                2 => context.BitwiseExclusiveOr(old, src),
                3 => context.BitwiseOr(old, src),
                4 => SelectMax(context, Extend(context, old, op.Size, true), Extend(context, src, op.Size, true), true),
                5 => SelectMin(context, Extend(context, old, op.Size, true), Extend(context, src, op.Size, true), true),
                6 => SelectMax(context, Extend(context, old, op.Size, false), Extend(context, src, op.Size, false), false),
                7 => SelectMin(context, Extend(context, old, op.Size, false), Extend(context, src, op.Size, false), false),
                _ => src,
            };

            value = MaskToSize(context, value, op.Size);

            Operand current = Cas(context, physAddr, old, value, op.Size);
            Operand equal = context.ICompareEqual(current, old);

            context.Copy(oldLocal, current);
            context.BranchIfFalse(retry, equal);
            context.MemoryBarrier();

            SetIntOrZR(context, op.Rt, is64 ? oldLocal : context.ZeroExtend32(OperandType.I64, oldLocal));
        }

        private static Operand EmitLoad(ArmEmitterContext context, Operand address, int size)
        {
            return size switch
            {
                0 => context.Load8(address),
                1 => context.Load16(address),
                2 => context.Load(OperandType.I32, address),
                _ => context.Load(OperandType.I64, address),
            };
        }

        private static Operand Cas(ArmEmitterContext context, Operand address, Operand expected, Operand desired, int size)
        {
            return size switch
            {
                0 => context.CompareAndSwap8(address, expected, desired),
                1 => context.CompareAndSwap16(address, expected, desired),
                _ => context.CompareAndSwap(address, expected, desired),
            };
        }

        private static Operand Extend(ArmEmitterContext context, Operand value, int size, bool signed)
        {
            if (size == 0)
            {
                return signed ? context.SignExtend8(value.Type, value) : context.ZeroExtend8(value.Type, value);
            }

            if (size == 1)
            {
                return signed ? context.SignExtend16(value.Type, value) : context.ZeroExtend16(value.Type, value);
            }

            return value;
        }

        private static Operand SelectMax(ArmEmitterContext context, Operand a, Operand b, bool signed)
        {
            Operand greater = signed ? context.ICompareGreater(a, b) : context.ICompareGreaterUI(a, b);

            return context.ConditionalSelect(greater, a, b);
        }

        private static Operand SelectMin(ArmEmitterContext context, Operand a, Operand b, bool signed)
        {
            Operand less = signed ? context.ICompareLess(a, b) : context.ICompareLessUI(a, b);

            return context.ConditionalSelect(less, a, b);
        }

        private static Operand MaskToSize(ArmEmitterContext context, Operand value, int size)
        {
            return size switch
            {
                0 => context.BitwiseAnd(value, Const(value.Type, 0xFF)),
                1 => context.BitwiseAnd(value, Const(value.Type, 0xFFFF)),
                _ => value,
            };
        }
    }
}
