using ARMeilleure.Decoders;
using ARMeilleure.IntermediateRepresentation;
using ARMeilleure.Translation;
using static ARMeilleure.Instructions.InstEmitHelper;
using static ARMeilleure.Instructions.InstEmitMemoryHelper;

namespace ARMeilleure.Instructions
{
    static partial class InstEmit
    {
        // ARMv8.1 CAS/CASA/CASL/CASAL. The old value goes to Rs.
        public static void Cas(ArmEmitterContext context)
        {
            OpCodeCas op = (OpCodeCas)context.CurrOp;

            bool is64 = op.Size == 3;
            Operand address = context.Copy(GetIntOrSP(context, op.Rn));
            Operand physAddr = EmitPtPointerLoad(context, address, default, write: true, op.Size);

            Operand expected = GetIntOrZR(context, op.Rs);
            Operand desired = GetIntOrZR(context, op.Rt);

            if (!is64)
            {
                expected = context.ConvertI64ToI32(expected);
                desired = context.ConvertI64ToI32(desired);
            }

            Operand old = op.Size switch
            {
                0 => context.CompareAndSwap8(physAddr, expected, desired),
                1 => context.CompareAndSwap16(physAddr, expected, desired),
                _ => context.CompareAndSwap(physAddr, expected, desired),
            };

            SetIntOrZR(context, op.Rs, is64 ? old : context.ZeroExtend32(OperandType.I64, old));
        }
    }
}
