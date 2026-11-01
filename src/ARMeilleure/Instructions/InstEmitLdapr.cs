using ARMeilleure.Decoders;
using ARMeilleure.IntermediateRepresentation;
using ARMeilleure.Translation;
using static ARMeilleure.Instructions.InstEmitHelper;
using static ARMeilleure.Instructions.InstEmitMemoryHelper;

namespace ARMeilleure.Instructions
{
    static partial class InstEmit
    {
        // ARMv8.3 LDAPRB/LDAPRH/LDAPR/LDAPRX (load-acquire RCpc).
        public static void Ldapr(ArmEmitterContext context)
        {
            OpCodeMem op = (OpCodeMem)context.CurrOp;

            Operand address = context.Copy(GetIntOrSP(context, op.Rn));
            Operand value = EmitReadIntAligned(context, address, op.Size);

            SetIntOrZR(context, op.Rt, value);

            context.MemoryBarrier();
        }
    }
}
