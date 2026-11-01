namespace ARMeilleure.Decoders
{
    class OpCodeBReg : OpCode
    {
        public int Rn { get; }

        public new static OpCode Create(InstDescriptor inst, ulong address, int opCode) => new OpCodeBReg(inst, address, opCode);

        // RETAA/RETAB encode LR as Rn=31; authentication is ignored, so decode as RET LR.
        public new static OpCode CreateRetAuth(InstDescriptor inst, ulong address, int opCode) => new OpCodeBReg(inst, address, unchecked((int)0xD65F03C0));

        public OpCodeBReg(InstDescriptor inst, ulong address, int opCode) : base(inst, address, opCode)
        {
            int op4 = (opCode >> 0) & 0x1f;
            int op2 = (opCode >> 16) & 0x1f;

            if (op2 != 0b11111 || op4 != 0b00000)
            {
                Instruction = InstDescriptor.Undefined;

                return;
            }

            Rn = (opCode >> 5) & 0x1f;
        }
    }
}
